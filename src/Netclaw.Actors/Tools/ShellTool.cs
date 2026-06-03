// -----------------------------------------------------------------------
// <copyright file="ShellTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Executes shell commands via /bin/bash (Linux) or cmd.exe (Windows).
/// Captures stdout+stderr, enforces timeout, closes stdin immediately.
/// </summary>
[NetclawTool(ToolName,
    "Execute a shell command and return stdout/stderr output with exit code",
    Grant = "shell")]
public sealed partial class ShellTool : NetclawTool<ShellTool.Params>
{
    public const string ToolName = "shell_execute";

    private readonly ToolConfig _config;
    private readonly ToolPathPolicy? _pathPolicy;
    private readonly ShellCommandPolicy? _commandPolicy;

    public record Params(
        [property: Description("The shell command to execute")] string Command,
        [property: Description("Working directory to run the command in (optional)")] string? WorkingDirectory = null);

    public ShellTool(ToolConfig config, ToolPathPolicy? pathPolicy = null, ShellCommandPolicy? commandPolicy = null)
    {
        _config = config;
        _pathPolicy = pathPolicy;
        _commandPolicy = commandPolicy;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => await ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Command))
            return "Error: 'command' parameter is required.";

        if (_commandPolicy is not null)
        {
            var commandDecision = _commandPolicy.Evaluate(args.Command);
            if (!commandDecision.Allowed)
                return $"Error: Command blocked by hard deny policy: {commandDecision.DenyReason}";
        }

        if (_pathPolicy?.CommandReferencesDeniedPath(args.Command, args.WorkingDirectory) == true)
            return "Error: Command references a protected file path. Access denied by security policy.";

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isWindows)
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(args.Command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(args.Command);
        }

        // Resolve working directory in priority order: explicit arg →
        // WorkingContext.ProjectDirectory (declared via set_working_directory)
        // → SessionDirectory (per-session scratch). Never falls through to
        // ProcessStartInfo's default of inheriting the daemon process's cwd —
        // that location is wherever the daemon happened to be launched and is
        // unrelated to what the agent is "working on," which makes it
        // impossible for the approval policy to reason about safe-space
        // membership. The matcher reads context.Cwd against the same
        // resolution chain so the gate evaluates folder-scoped ApprovalEntry
        // records against the directory the spawned process will run in.
        var resolvedCwd = context.ResolveShellCwd(args.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(resolvedCwd))
        {
            if (IsResolvedSessionDirectory(resolvedCwd, context.SessionDirectory))
            {
                try
                {
                    Directory.CreateDirectory(resolvedCwd);
                }
                catch (Exception ex) when (ex is ArgumentException
                                           or IOException
                                           or NotSupportedException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException)
                {
                    return $"Error preparing session working directory: {ex.Message}";
                }
            }
            else if (!Directory.Exists(resolvedCwd))
            {
                // ProcessStartInfo.WorkingDirectory must point at an existing directory or
                // Process.Start throws an opaque, platform-specific error. Only the session
                // scratch dir is auto-created (above); every other resolved cwd — explicit
                // arg, project dir, inherited cwd — must already exist. Fail loudly with the
                // remedy so the agent creates it instead of retry-looping on a cryptic error.
                // Any approval for this cwd is existence-agnostic, so it still matches once
                // the agent runs the mkdir.
                if (File.Exists(resolvedCwd))
                    return $"Error: Working directory '{resolvedCwd}' is a file, not a directory.";

                var mkdirHint = isWindows ? $"mkdir \"{resolvedCwd}\"" : $"mkdir -p \"{resolvedCwd}\"";
                return $"Error: Working directory '{resolvedCwd}' does not exist. "
                     + $"Create it first, e.g.: {mkdirHint}";
            }

            psi.WorkingDirectory = resolvedCwd;
        }

        var effectiveTimeoutSeconds = context.RequestedTimeoutSeconds is > 0
            ? context.RequestedTimeoutSeconds.Value
            : _config.ShellTimeoutSeconds;

        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            return $"Error starting process: {ex.Message}";
        }

        // Start the timeout countdown only after the shell process exists, so
        // process-spawn overhead (heavier on Windows: cmd.exe plus the child it
        // execs) is not charged against the command's execution budget.
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSeconds));

        using (process)
        {
            process.StandardInput.Close();

            // Start draining both pipes up front: a chatty child can deadlock if
            // one pipe buffer fills while we wait on the other. The reads take
            // CancellationToken.None deliberately — a redirected child holds the
            // pipe write-ends open, so a blocked pipe read cannot be interrupted
            // by a token; killing the process is what closes the pipes.
            //
            // BoundedDrainAsync reads into a head+tail window bounded by
            // MaxOutputChars but continues draining after the cap is reached so
            // the pipe never fills up and deadlocks a still-running child.
            var stdoutTask = BoundedDrainAsync(process.StandardOutput, _config.MaxOutputChars);
            var stderrTask = BoundedDrainAsync(process.StandardError, _config.MaxOutputChars);

            try
            {
                // WaitForExitAsync waits on the process-exit event, not a pipe
                // read, so it honors the token — and a process that exits cleanly
                // completes it normally even if the deadline trips a moment later,
                // so a finished command is never mislabelled as a timeout.
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                var killClosedPipes = true;

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException ex)
                {
                    // Already exited between cancellation detection and kill.
                    Debug.WriteLine($"shell_execute: process kill skipped — {ex.Message}");
                }
                catch (Win32Exception ex)
                {
                    // If the OS refuses the kill, the child may keep stdout/stderr
                    // open forever. Close our read ends so cancellation still
                    // returns promptly instead of hanging in BoundedDrainAsync.
                    killClosedPipes = false;
                    Debug.WriteLine($"shell_execute: process kill skipped — {ex.Message}");
                    process.StandardOutput.Dispose();
                    process.StandardError.Dispose();
                }

                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
                catch (Exception ex) when (!killClosedPipes && ex is IOException or ObjectDisposedException)
                {
                    Debug.WriteLine($"shell_execute: pipe drain aborted — {ex.Message}");
                }

                return timeoutCts.IsCancellationRequested
                    ? $"Error: Command timed out after {effectiveTimeoutSeconds} seconds."
                    : "Error: Command cancelled.";
            }

            var (stdoutText, stdoutTruncated) = await stdoutTask;
            var (stderrText, stderrTruncated) = await stderrTask;

            // Redact secrets on the already-bounded strings, then assemble.
            var stdout = SecretOutputRedactor.Redact(stdoutText);
            var stderr = SecretOutputRedactor.Redact(stderrText);

            var result = new StringBuilder();
            if (stdout.Length > 0)
            {
                result.Append(stdout);
                if (stdoutTruncated)
                    result.Append($"\n[stdout truncated — output exceeded {_config.MaxOutputChars} chars; head and tail shown]");
            }
            if (stderr.Length > 0)
            {
                if (result.Length > 0)
                    result.AppendLine();
                result.Append(stderr);
                if (stderrTruncated)
                    result.Append($"\n[stderr truncated — output exceeded {_config.MaxOutputChars} chars; head and tail shown]");
            }

            return $"Exit code: {process.ExitCode}{Environment.NewLine}{result}";
        }
    }

    /// <summary>
    /// Drains <paramref name="reader"/> into a head+tail window bounded by
    /// <paramref name="maxChars"/>. Chars beyond the cap are discarded but the
    /// pipe continues to be read so a still-running child never deadlocks on a
    /// full pipe buffer. Returns the captured text and whether it was truncated.
    /// </summary>
    /// <remarks>
    /// Allocation is O(<paramref name="maxChars"/>), not O(total output): the
    /// scratch read buffer is pooled, the tail ring is allocated only if the head
    /// fills, and reads go through the <see cref="ValueTask{T}"/> overload so a
    /// pipe that already has data buffered completes synchronously without a
    /// per-chunk <see cref="Task"/> allocation. A child that prints hundreds of MB
    /// is drained with a handful of KB of managed allocations — the property that
    /// keeps a memory-limited daemon from being OOM-killed (see #1293).
    /// </remarks>
    internal static async Task<(string Text, bool Truncated)> BoundedDrainAsync(
        TextReader reader, int maxChars)
    {
        if (maxChars <= 0)
        {
            // Explicit opt-out: a non-positive cap disables truncation and reads
            // the whole stream. The config schema enforces MaxOutputChars >= 1, so
            // production never reaches this — it exists only for programmatic
            // callers that deliberately pass 0/negative to capture full output.
            var all = await reader.ReadToEndAsync(CancellationToken.None);
            return (all, false);
        }

        // Split the budget: first half for the head, second half for the tail.
        // Odd maxChars gives the extra char to the head. Computed without (maxChars
        // + 1) so a near-int.MaxValue cap can't overflow to a negative headCap.
        var headCap = maxChars / 2 + maxChars % 2;
        var tailCap = maxChars / 2;

        var head = new StringBuilder(Math.Min(headCap, 4096));

        // Tail ring buffer, allocated lazily on first overflow past the head. The
        // common case — output under the cap — never fills the head, so it never
        // pays for the tail window at all.
        char[]? tailBuf = null;
        var tailStart = 0;  // index of the oldest retained char in the ring
        var tailLen = 0;    // chars currently retained (<= tailCap)

        long totalChars = 0; // total chars seen across all reads; long so a multi-GB
                             // flood can't overflow the truncation check to a false negative

        // Transient scratch buffer for the read loop: pooled so a long drain
        // doesn't allocate it per call and it never lands on the LOH.
        var buf = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            int read;
            // ReadAsync(Memory<char>) returns a non-allocating ValueTask when the
            // read completes synchronously (data already buffered) — unlike the
            // Task<int> char[] overload, which allocates once per chunk.
            while ((read = await reader.ReadAsync(buf.AsMemory(), CancellationToken.None)) > 0)
            {
                totalChars += read;
                var span = buf.AsSpan(0, read);

                if (head.Length < headCap)
                {
                    var headChunk = Math.Min(headCap - head.Length, span.Length);
                    head.Append(span[..headChunk]);
                    span = span[headChunk..];
                }

                if (span.IsEmpty || tailCap == 0)
                    continue;

                tailBuf ??= new char[tailCap];
                AppendToTailRing(tailBuf, span, ref tailStart, ref tailLen);
            }
        }
        finally
        {
            // clearArray: the scratch buffer held raw stdout/stderr (possibly
            // secrets); wipe it before returning to the shared pool.
            ArrayPool<char>.Shared.Return(buf, clearArray: true);
        }

        // Truncation only when total chars exceeded the full budget (head+tail),
        // meaning some middle chars were discarded.
        var truncated = totalChars > maxChars;

        // Reconstruct in place on `head`: when truncated, the discarded middle is
        // marked with a separator; otherwise head + tail is the full output. Reusing
        // `head` avoids a second StringBuilder and re-copying the head.
        if (truncated)
            head.Append("\n...\n");
        if (tailBuf is not null && tailLen > 0)
            AppendRing(head, tailBuf, tailStart, tailLen);
        return (head.ToString(), truncated);
    }

    /// <summary>
    /// Writes <paramref name="span"/> into a ring buffer that retains only the
    /// most recent <c>ring.Length</c> chars. Uses block copies (at most two per
    /// call) rather than a per-char loop, so draining a very chatty child stays
    /// cheap regardless of how much it prints.
    /// </summary>
    private static void AppendToTailRing(char[] ring, ReadOnlySpan<char> span, ref int start, ref int len)
    {
        var cap = ring.Length;

        if (span.Length >= cap)
        {
            // This span alone fills (or overfills) the window: only its last `cap`
            // chars can survive. One contiguous copy, ring reset.
            span[^cap..].CopyTo(ring);
            start = 0;
            len = cap;
            return;
        }

        var writePos = (start + len) % cap;
        var first = Math.Min(span.Length, cap - writePos);
        span[..first].CopyTo(ring.AsSpan(writePos));
        if (first < span.Length)
            span[first..].CopyTo(ring); // remainder wraps to the front

        var newLen = len + span.Length;
        if (newLen > cap)
        {
            // Overwrote the oldest chars: advance start past them.
            start = (start + (newLen - cap)) % cap;
            len = cap;
        }
        else
        {
            len = newLen;
        }
    }

    /// <summary>Appends a ring buffer's retained chars, oldest-first, to <paramref name="sb"/>.</summary>
    private static void AppendRing(StringBuilder sb, char[] ring, int start, int len)
    {
        var first = Math.Min(len, ring.Length - start);
        sb.Append(ring, start, first);
        if (first < len)
            sb.Append(ring, 0, len - first);
    }

    // Retained for compatibility with tests that call it directly; the main
    // execution path no longer uses this — BoundedDrainAsync caps at read time.
    internal static string TruncateOutput(string output, int maxChars)
    {
        if (output.Length <= maxChars)
            return output;

        return string.Concat(output.AsSpan(0, maxChars), $"{Environment.NewLine}... [output truncated]");
    }

    private static bool IsResolvedSessionDirectory(string resolvedCwd, string? sessionDirectory)
        => !string.IsNullOrWhiteSpace(sessionDirectory)
           && PathUtility.AreEquivalentPaths(resolvedCwd, sessionDirectory);
}
