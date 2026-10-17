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

    public static async Task<HeadlessRun> RunAsync(
        DaemonClient client,
        DisposableTempDir directory,
        bool json,
        CancellationToken stopping,
        string prompt = "Do the task.")
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

            // The channel calls StopApplication as its last act on every path. It writes the console
            // output and sets the exit code first. So this signal means that the channel finished its
            // own writes.
            await stopped.Task.WaitAsync(StopCeiling, TestContext.Current.CancellationToken);
            await channel.StopAsync(CancellationToken.None);

            // The actor system and the host logger write to the console on their own threads. Detach the
            // capture first, then take the snapshot under the writer lock. A write in flight finishes
            // before the snapshot. A later write goes to the original console.
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
            _buffer.Append(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
            _buffer.Append(buffer, index, count);
    }

    public override void Write(string? value)
    {
        lock (_gate)
            _buffer.Append(value);
    }

    public string Snapshot()
    {
        lock (_gate)
            return _buffer.ToString();
    }
}
