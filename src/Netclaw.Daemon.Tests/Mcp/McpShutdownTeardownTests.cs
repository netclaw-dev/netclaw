// -----------------------------------------------------------------------
// <copyright file="McpShutdownTeardownTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// Real-process proofs for the mcp-shutdown-teardown change
/// (openspec/changes/mcp-shutdown-teardown, GitHub #1667): idempotent
/// double-teardown, no reconnect once teardown has started, parallel
/// multi-server teardown, and clean failure for a tool call in flight when
/// its client is disposed. Uses <see cref="McpSmokeHarness"/>'s real spawned
/// child MCP server processes throughout — no faked transports or clients.
/// </summary>
public sealed class McpShutdownTeardownTests
{
    [Fact]
    public async Task DoubleTeardown_DisposesChildProcessExactlyOnce_WithoutSpuriousWarnings()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var recordingLogger = new RecordingLogger();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["teardown_probe"] = CreateEntry() },
            new ToolRegistry(),
            recordingLogger);

        await harness.Manager.StartAsync(cts.Token);

        var info = await GetProcessInfoAsync(harness, "teardown_probe", "slack/channel/thread", cts.Token);
        using var process = Process.GetProcessById(info.ProcessId);

        // Simulate the real shutdown sequence this change introduces:
        // ApplicationStopping (the new hook, registered in StartAsync)
        // fires first, then the host's normal IHostedService.StopAsync
        // runs afterward regardless of which one actually did the work.
        harness.AppLifetime.StopApplication();
        await harness.Manager.StopAsync(cts.Token);

        // A third entry point — IDisposable.Dispose, e.g. DI container
        // teardown — must also converge on the same completed work rather
        // than redoing (or racing) disposal.
        harness.Manager.Dispose();

        await process.WaitForExitAsync(cts.Token);
        Assert.True(process.HasExited);

        // The success log fires exactly once — by whichever of the three
        // callers actually did the work — never once per caller.
        Assert.Single(recordingLogger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("MCP clients shut down", StringComparison.Ordinal));

        // The second and third entries observe already-disposed state and
        // must not log a warning or error for it.
        Assert.DoesNotContain(recordingLogger.Entries, e => e.Level is LogLevel.Warning or LogLevel.Error);
    }

    [Fact]
    public async Task TryReconnectAsync_AfterTeardownStarted_ReturnsFalseAndCreatesNoNewClient()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var serverName = new McpServerName("teardown_probe");
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { [serverName.Value] = CreateEntry() },
            new ToolRegistry());

        await harness.Manager.StartAsync(cts.Token);

        var originalClient = harness.Manager.GetClient(serverName);
        Assert.NotNull(originalClient);

        // ApplicationLifetime.StopApplication() cancels the ApplicationStopping
        // token synchronously on this thread, which synchronously invokes our
        // registered callback and sets McpClientManager's _stopping flag —
        // all before this call returns. There is no race window to wait out
        // before the next line: TryReconnectAsync is guaranteed to observe
        // _stopping == true.
        harness.AppLifetime.StopApplication();

        var reconnected = await harness.Manager.TryReconnectAsync(serverName, cts.Token);
        Assert.False(reconnected);

        // Had the guard not engaged, TryReconnectAsync would have removed
        // the original client, spawned a brand-new child process, and
        // installed a new McpClient in its place. Instead the slot is
        // either untouched (the background teardown triggered by
        // StopApplication() hasn't reached it yet) or cleared to null by
        // that same teardown — never replaced with something new. Since a
        // new child process can only come into existence via a new McpClient
        // (ConnectAsync creates the transport — and its process — before
        // installing the client), this is a direct proof no process spawned.
        var clientAfterReconnectAttempt = harness.Manager.GetClient(serverName);
        Assert.True(
            clientAfterReconnectAttempt is null || ReferenceEquals(clientAfterReconnectAttempt, originalClient),
            "TryReconnectAsync must not install a new client once teardown has started.");

        await harness.Manager.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ParallelTeardown_DisposesConfiguredServersConcurrently()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry>
            {
                ["teardown_probe_a"] = CreateEntry(),
                ["teardown_probe_b"] = CreateEntry(),
            },
            new ToolRegistry());

        await harness.Manager.StartAsync(cts.Token);

        // Each configured server's dispose is dominated by a real, fixed
        // cost inherent to the MCP SDK, not something this test injects:
        // StdioClientSessionTransport.CleanupAsync (see
        // ModelContextProtocol.Client.StdioClientSessionTransport, v1.4.1)
        // idly awaits the child process's own exit for up to
        // StdioClientTransportOptions.ShutdownTimeout (hardcoded to 10s in
        // McpClientManager.CreateTransport) before force-killing the
        // process tree — the SDK never proactively closes the child's
        // stdin first, so a healthy smoke-server process (which never
        // exits on its own) reliably burns close to that full 10s per
        // dispose. Two servers therefore give a real, deterministic
        // ~10s-per-client cost: ~10s total if disposed in parallel,
        // ~20s if disposed sequentially — a large enough gap that a
        // generous bound comfortably distinguishes the two without
        // flaking on a slow CI runner.
        var stopwatch = Stopwatch.StartNew();
        await harness.Manager.StopAsync(cts.Token);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(16),
            $"Expected concurrent teardown of 2 servers to take roughly one ~10s dispose, not the ~20s sum of two sequential disposes. Actual: {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task InFlightToolCall_FailsCleanly_WhenTeardownDisposesItsClient()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["teardown_probe"] = CreateEntry() },
            registry);

        await harness.Manager.StartAsync(cts.Token);

        // Go through the registered McpToolAdapter (not McpClientManager
        // directly) so this test exercises the exact path a real session
        // uses, including McpToolAdapter.ExecuteAsync's catch that formats
        // any invoker exception into the clean "Error: MCP tool '...'
        // failed" result.
        var tool = registry.GetByName("teardown_probe/sleep");
        Assert.NotNull(tool);

        var invocation = TestToolExecutionContext
            .CreateBound("slack/channel/thread", null, TrustAudience.Personal)
            .Invocation;

        // 30s is far longer than this test needs to wait: McpSessionHandler
        // cancels every outstanding request's TaskCompletionSource as the
        // very first step of McpClient.DisposeAsync (before any process
        // teardown), so the in-flight call fails within milliseconds of
        // StopAsync starting — well before the tool's own 30s would elapse
        // and well before the SDK's ~10s process-kill timeline.
        var callTask = tool!.ExecuteAsync(
            new Dictionary<string, object?> { ["ms"] = 30_000 }, invocation, cts.Token);

        // No delay before triggering teardown: ExecuteAsync's async call
        // chain (McpToolAdapter -> McpClientManager -> the MCP SDK's
        // SendRequestAsync) runs synchronously on this thread up to its
        // first genuine suspension point — which is after the pending
        // request is already registered — before control returns here.
        await harness.Manager.StopAsync(cts.Token);

        var result = await callTask.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.StartsWith("Error: MCP tool 'teardown_probe/sleep' failed:", result, StringComparison.Ordinal);
    }

    private static McpServerEntry CreateEntry()
        => new()
        {
            Transport = "stdio",
            Command = "dotnet",
            Arguments = [SmokeMcpServerLocator.LocateDll()],
            Enabled = true,
        };

    private static async Task<ProcessInfo> GetProcessInfoAsync(
        McpSmokeHarness harness,
        string serverName,
        string sessionId,
        CancellationToken ct)
    {
        var result = await harness.Manager.InvokeAsync(
            serverName,
            "process-info",
            null,
            TestToolExecutionContext.CreateBound(sessionId, null, TrustAudience.Personal).Invocation,
            ct);

        return JsonSerializer.Deserialize<ProcessInfo>(result, JsonOptions)!;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record ProcessInfo(int ProcessId, string[] Arguments);

    /// <summary>
    /// Captures every log call made through it so tests can assert on
    /// message content and level without a mocking framework. Thread-safe:
    /// parallel teardown logs from multiple concurrent dispose tasks.
    /// </summary>
    private sealed class RecordingLogger : ILogger<McpClientManager>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_entries)
                _entries.Add((logLevel, message));
        }
    }
}
