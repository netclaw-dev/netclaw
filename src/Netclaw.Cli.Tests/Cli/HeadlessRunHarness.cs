// -----------------------------------------------------------------------
// <copyright file="HeadlessRunHarness.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Serializes every test that redirects <see cref="Console"/> or sets
/// <see cref="Environment.ExitCode"/>. Both are process-wide.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessProcessStateCollection
{
    public const string Name = "Headless process state";
}

/// <summary>What one <c>chat -p</c> run wrote and returned.</summary>
internal sealed record HeadlessRun(
    string Stdout,
    string Stderr,
    int ExitCode,
    string SessionLog,
    string FailureLog)
{
    /// <summary>
    /// The stable failure line on stderr. A harness parses it. See the error category contract in
    /// <c>HeadlessTurnErrorCategoryTests</c>.
    /// </summary>
    public IReadOnlyList<string> TurnFailedLines => Stderr
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.StartsWith("[turn:failed]", StringComparison.Ordinal))
        .ToArray();

    /// <summary>The <c>TURN_FAILED</c> records in the session log.</summary>
    public IReadOnlyList<string> TurnFailedLogRecords => SessionLog
        // The channel writes each record with WriteLine. The line ending is "\r\n" on Windows.
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.Contains("TURN_FAILED:", StringComparison.Ordinal))
        .ToArray();

    /// <summary>The JSON envelope that <c>--json</c> prints, or null when stdout holds no JSON.</summary>
    public JsonDocument? Envelope
    {
        get
        {
            // The in-process actor system writes its own log lines to the same console. The envelope is
            // the last line that holds a JSON object.
            var line = Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(candidate => candidate.Trim())
                .LastOrDefault(candidate => candidate.StartsWith('{') && candidate.EndsWith('}'));
            return line is null ? null : JsonDocument.Parse(line);
        }
    }
}

/// <summary>
/// Runs a real <see cref="HeadlessChannel"/> against a <see cref="DaemonClient"/> and captures what a
/// shell script sees: stdout, stderr, the process exit code, and the session log.
/// </summary>
internal static class HeadlessRunHarness
{
    // A fault ceiling. A run that never stops fails here. No test orchestrates with this wait.
    private static readonly TimeSpan StopCeiling = TimeSpan.FromSeconds(30);

    /// <param name="drive">
    /// Optional script that runs while the channel runs. It waits on events (a stdout line, the stop of the
    /// run), never on a delay. The harness awaits it after the run stops, so a failed step in it fails the test.
    /// </param>
    public static async Task<HeadlessRun> RunAsync(
        DaemonClient client,
        DisposableTempDir directory,
        bool json,
        CancellationToken stopping,
        string prompt = "Do the task.",
        Func<HeadlessDrive, Task>? drive = null)
    {
        var paths = new NetclawPaths(directory.Path);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new HeadlessChannel(
            client,
            paths,
            new SignalingLifetime(stopped, stopping),
            TimeProvider.System,
            new HeadlessOptions(prompt) { JsonOutput = json },
            NullLogger<HeadlessChannel>.Instance);

        var stdout = new LockedCapture();
        var stderr = new LockedCapture();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Environment.ExitCode = 0;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            await channel.StartAsync(TestContext.Current.CancellationToken);
            var driver = drive is null
                ? Task.CompletedTask
                : Task.Run(() => drive(new HeadlessDrive(stdout, stopped.Task, paths)), CancellationToken.None);

            // The channel calls StopApplication as its last act on every path. It writes the console
            // output and sets the exit code first. So this signal means that the channel finished its
            // own writes. StopAsync only marks the channel as disconnected. It does not drain output.
            await stopped.Task.WaitAsync(StopCeiling, TestContext.Current.CancellationToken);
            await channel.StopAsync(CancellationToken.None);
            await driver.WaitAsync(StopCeiling, TestContext.Current.CancellationToken);

            // The Akka StandardOutLogger reads Console.Out on each call, so it can write to the capture on
            // its own thread. The ASP.NET console logger keeps the Console.Out that it got before
            // Console.SetOut, so it never writes to the capture. Detach the capture first, then take the
            // snapshot under the writer lock. A write in flight finishes before the snapshot. A later write
            // that reads Console.Out goes to the original console. A writer that kept a reference to the
            // capture still writes to it, under the lock, after the snapshot.
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            return new HeadlessRun(
                stdout.Snapshot(),
                stderr.Snapshot(),
                Environment.ExitCode,
                ReadSessionLog(paths),
                ReadFile(Path.Combine(paths.LogsDirectory, "headless-errors.log")));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = 0;
        }
    }

    private static string ReadSessionLog(NetclawPaths paths)
    {
        if (!Directory.Exists(paths.LogsDirectory))
            return string.Empty;

        // The channel names its log after the session id. A run has exactly one session.
        var logs = Directory.GetFiles(paths.LogsDirectory, "*.log")
            .Where(path => !string.Equals(Path.GetFileName(path), "headless-errors.log", StringComparison.Ordinal))
            .ToArray();
        return logs.Length == 0 ? string.Empty : ReadFile(Assert.Single(logs));
    }

    private static string ReadFile(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        // The channel keeps its log writer open until after it stops the host.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class SignalingLifetime(TaskCompletionSource stopped, CancellationToken stopping)
        : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopped.TrySetResult();
    }
}

/// <summary>What a script sees while a headless run is in progress.</summary>
internal sealed class HeadlessDrive(LockedCapture stdout, Task stopped, NetclawPaths paths)
{
    /// <summary>The task that completes when the channel stops the host.</summary>
    public Task Stopped => stopped;

    /// <summary>
    /// Waits until stdout holds <paramref name="text"/>. Returns false when the run stopped first. The
    /// caller then ends its script, and the assertions on the finished run report the cause.
    /// </summary>
    public async Task<bool> StdoutShowsAsync(string text)
    {
        var seen = stdout.WhenContainsAsync(text);
        return await Task.WhenAny(seen, stopped) == seen;
    }

    /// <summary>The stdout text so far.</summary>
    public string Stdout => stdout.Snapshot();

    /// <summary>
    /// The session id, read from the first record of the session log. The channel writes that record before
    /// it sends the prompt.
    /// </summary>
    public string SessionId()
    {
        var log = Assert.Single(
            Directory.GetFiles(paths.LogsDirectory, "*.log"),
            path => !string.Equals(Path.GetFileName(path), "headless-errors.log", StringComparison.Ordinal));
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        const string marker = "Headless session started: ";
        var line = reader.ReadLine() ?? string.Empty;
        return line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
    }
}

/// <summary>
/// A <see cref="TextWriter"/> that keeps its text in a buffer. Every write and the snapshot take one lock,
/// so a thread that reads the text never sees a write in progress. A plain <see cref="StringWriter"/> has
/// no lock for <c>ToString</c>, and <see cref="Console.SetOut(TextWriter)"/> locks only its own wrapper.
/// </summary>
internal sealed class LockedCapture : TextWriter
{
    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_gate)
        {
            _buffer.Append(value);
            Notify();
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            _buffer.Append(buffer, index, count);
            Notify();
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            _buffer.Append(value);
            Notify();
        }
    }

    private readonly List<(string Text, TaskCompletionSource Seen)> _waiters = [];

    public string Snapshot()
    {
        lock (_gate)
            return _buffer.ToString();
    }

    /// <summary>A task that completes once the text holds <paramref name="text"/>. No timer is involved.</summary>
    public Task WhenContainsAsync(string text)
    {
        lock (_gate)
        {
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_buffer.ToString().Contains(text, StringComparison.Ordinal))
                seen.TrySetResult();
            else
                _waiters.Add((text, seen));
            return seen.Task;
        }
    }

    private void Notify()
    {
        // Called with the gate held.
        if (_waiters.Count == 0)
            return;
        var text = _buffer.ToString();
        _waiters.RemoveAll(waiter =>
            text.Contains(waiter.Text, StringComparison.Ordinal) && waiter.Seen.TrySetResult());
    }
}
