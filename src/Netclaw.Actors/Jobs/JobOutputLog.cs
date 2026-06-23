// -----------------------------------------------------------------------
// <copyright file="JobOutputLog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Security;

namespace Netclaw.Actors.Jobs;

/// <summary>
/// Streams a background job's output to its on-disk log as the process produces
/// it, so the log is observable (file_read/grep/check_background_job) while the
/// job runs — a background job is a detached process with no completion
/// expectation, so exit time is too late to make output visible.
/// Lines are secret-redacted at write time (per line — a secret spanning a line
/// boundary would evade THIS pass; the redactor's patterns are mostly
/// token-shaped). The on-disk log therefore inherits a per-line redaction
/// limitation, but every place a tail is surfaced to the model (completion
/// delivery, check_background_job, lost-job notification) re-runs the redactor
/// over the assembled multi-line tail, so multi-line secrets cannot reach the
/// LLM even though they may persist in the file (file access is trust-gated).
/// Disk is bounded by single-slot rotation: when the current log crosses the
/// threshold it moves to the `.1` slot (replacing any earlier rotation), so a
/// chatty long-running process holds at most ~2x the threshold on disk and the
/// most recent output is always in the current log.
/// </summary>
public sealed class JobOutputLog : IAsyncDisposable
{
    public const long DefaultRotationThresholdBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly long _rotationThresholdBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StreamWriter? _writer;
    private long _bytesWritten;

    // Read un-gated on the WriteLineAsync fast path (below) from either pump
    // thread while written under the gate on the other; volatile gives that
    // cross-thread read a happens-before edge so it can't observe a stale null.
    private volatile string? _writeFailure;

    public bool Rotated { get; private set; }

    /// <summary>
    /// First write failure, if any. Once a write fails the log stops accepting
    /// lines but callers MUST keep draining the process pipes — a child blocked
    /// on a full pipe never exits. The failure is surfaced on the completion
    /// message so the broken capture is loud, not silent. A transient rotation
    /// (File.Move) hiccup does NOT set this — see <see cref="Rotate"/>.
    /// </summary>
    public string? WriteFailure => _writeFailure;

    public JobOutputLog(string path, long rotationThresholdBytes = DefaultRotationThresholdBytes)
    {
        _path = path;
        _rotationThresholdBytes = rotationThresholdBytes;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Eager-create so the path handed to the agent in the submit ACK is
        // readable from the moment the job starts, not after first output.
        _writer = OpenWriter();
    }

    public string RotatedPath => RotatedPathFor(_path);

    public static string RotatedPathFor(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, $"{stem}.1{ext}");
    }

    public async Task WriteLineAsync(string line, bool isStderr)
    {
        if (_writeFailure is not null)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _writer ??= OpenWriter();
            var redacted = SecretOutputRedactor.Redact(line);
            if (isStderr)
                redacted = "[stderr] " + redacted;

            // AutoFlush=true: each line is flushed to the OS so the log is
            // live-readable mid-run (waiting for a "server ready" line is the
            // core use case). This is a write() to the page cache per line, not
            // an fsync — cheap for the intended workload; a time-throttled flush
            // was rejected because it can leave a final, quiescent ready-line
            // unflushed and invisible to a poller.
            await _writer.WriteLineAsync(redacted).ConfigureAwait(false);
            _bytesWritten += Encoding.UTF8.GetByteCount(redacted) + Environment.NewLine.Length;

            if (_bytesWritten >= _rotationThresholdBytes)
                Rotate();
        }
        catch (Exception ex)
        {
            _writeFailure = ex.Message;
            try
            {
                _writer?.Dispose();
            }
            catch // slopwatch-ignore: SW003 writer already broken from the original failure, which is what gets reported via WriteFailure
            {
            }

            _writer = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _writer?.Dispose();
            _writer = null;
        }
        catch (Exception ex)
        {
            _writeFailure ??= ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Bounded tail read: seeks from the end instead of loading the whole file,
    /// so querying a multi-megabyte log costs O(maxChars). Reads the current log;
    /// if it is momentarily absent because a concurrent <see cref="Rotate"/> is
    /// mid-File.Move, falls back to the rotated `.1` predecessor (where the bytes
    /// just landed) rather than reporting an empty tail.
    /// </summary>
    public static (string Tail, bool Truncated) ReadTail(string path, int maxChars)
    {
        try
        {
            return ReadTailFrom(path, maxChars);
        }
        catch (FileNotFoundException)
        {
            // Rotation window: the current log was just moved to the `.1` slot
            // and the fresh current file is not open yet. The most-recent bytes
            // are in the rotated predecessor — read those instead of throwing.
            var rotated = RotatedPathFor(path);
            if (File.Exists(rotated))
                return ReadTailFrom(rotated, maxChars);
            throw;
        }
    }

    private static (string Tail, bool Truncated) ReadTailFrom(string path, int maxChars)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (fs.Length == 0)
            return (string.Empty, false);

        // 4 bytes/char is the UTF-8 worst case; log content is overwhelmingly
        // ASCII so this comfortably over-fetches the requested char count.
        var seekBytes = Math.Min(fs.Length, maxChars * 4L);
        fs.Seek(-seekBytes, SeekOrigin.End);
        var buffer = new byte[seekBytes];
        fs.ReadExactly(buffer);

        var text = Encoding.UTF8.GetString(buffer);
        // A seek landing mid-codepoint decodes to a replacement char at the
        // very start; trim it rather than show mojibake in a tail view.
        text = text.TrimStart('�');
        if (text.Length > maxChars)
            text = text[^maxChars..];

        return (text, fs.Length > seekBytes);
    }

    private StreamWriter OpenWriter() =>
        new(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        try
        {
            File.Move(_path, RotatedPath, overwrite: true);
            Rotated = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) // slopwatch-ignore: SW003 transient rotation failure is deliberately non-fatal — capture continues on the current log (see below)
        {
            // A transient move failure (e.g. an AV scanner / indexer holding the
            // `.1` target on Windows) must NOT kill capture for the rest of a
            // possibly hours-long job. The current log was not moved, so keep
            // appending to it and retry rotation after another threshold's worth
            // of output. The log can briefly exceed the cap — strictly better
            // than silently losing all subsequent output. This is deliberately
            // NOT a WriteFailure: the pipe is healthy and capture continues.
        }
        finally
        {
            // Reset on both paths: a clean rotation starts a fresh current log at
            // 0; a failed rotation defers the retry by a full threshold instead
            // of hammering File.Move on every line.
            _bytesWritten = 0;
            _writer = OpenWriter();
        }
    }
}
