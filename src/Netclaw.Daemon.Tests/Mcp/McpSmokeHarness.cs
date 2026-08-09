// -----------------------------------------------------------------------
// <copyright file="McpSmokeHarness.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// Builds and owns an <see cref="McpClientManager"/> for the MCP smoke tests:
/// wires the OAuth scaffolding the constructor requires, and on
/// <see cref="DisposeAsync"/> tears down the whole graph — the manager, its
/// child MCP server processes, and the HTTP clients. Each test creates its own
/// harness so the spawned MCP processes stay isolated per test.
/// </summary>
internal sealed class McpSmokeHarness : IAsyncDisposable
{
    private readonly McpOAuthFlowBroker _flowBroker;

    private McpSmokeHarness(McpClientManager manager, McpOAuthFlowBroker flowBroker)
    {
        Manager = manager;
        _flowBroker = flowBroker;
    }

    public McpClientManager Manager { get; }

    /// <summary>
    /// Asserts that the named MCP server reached the <see cref="McpConnectionState.Connected"/>
    /// state after <see cref="Manager.StartAsync"/> completed. `StartAsync` awaits the whole
    /// connect attempt — either tools are published to the registry or a failure status with
    /// the underlying error is published — so there is nothing to poll: this is the
    /// deterministic completion signal. Asserting on it turns an intermittent Windows CI
    /// connect failure (previously a bare `Assert.NotNull` null on the tool lookup) into a
    /// failure that carries the manager's actual error message.
    /// </summary>
    public void AssertConnected(string serverName)
    {
        var status = Manager.GetServerStatuses().GetValueOrDefault(new McpServerName(serverName));
        Assert.NotNull(status);
        Assert.True(
            status.State is McpConnectionState.Connected,
            $"MCP server '{serverName}' failed to connect: state={status.State}, " +
            $"error={status.ErrorMessage ?? "(none)"}");
    }

    public static McpSmokeHarness Create(
        Dictionary<string, McpServerEntry> serverEntries,
        ToolRegistry registry,
        ITestOutputHelper? output = null)
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        paths.EnsureDirectoriesExist();
        var credentials = new McpOAuthCredentialStore(
            paths,
            TimeProvider.System,
            new NullSecretsProtector(),
            NullLogger<McpOAuthCredentialStore>.Instance);
        var flowBroker = new McpOAuthFlowBroker(TimeProvider.System, CancellationToken.None);
        var manager = new McpClientManager(
            serverEntries,
            registry,
            new ToolConfig(),
            credentials,
            McpOAuthTestDoubles.UnusedRegistrar(),
            flowBroker,
            new DaemonConfig(),
            NullNotificationSink.Instance,
            TimeProvider.System,
            new McpClientRuntime(),
            // Real logger wired to test output: when a connect fails the manager
            // logs the full exception via ReportConnectionFailure, and NullLogger
            // was discarding it — leaving only the generic status ErrorMessage.
            output is null
                ? NullLogger<McpClientManager>.Instance
                : new TestOutputLogger<McpClientManager>(output),
            new SessionConfig());
        return new McpSmokeHarness(manager, flowBroker);
    }

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that forwards to xunit test output so
    /// the manager's own diagnostics (including the full connect-failure
    /// exception) show up in the CI log when a smoke test fails.
    /// </summary>
    private sealed class TestOutputLogger<T> : ILogger<T>
    {
        private readonly ITestOutputHelper _output;

        public TestOutputLogger(ITestOutputHelper output) => _output = output;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _output.WriteLine($"[{logLevel}] {message}");
            if (exception is not null)
                _output.WriteLine(exception.ToString());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.StopAsync(CancellationToken.None);
        Manager.Dispose();
        _flowBroker.Dispose();
    }
}
