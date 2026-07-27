// -----------------------------------------------------------------------
// <copyright file="McpSmokeHarness.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;

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

    public static McpSmokeHarness Create(
        Dictionary<string, McpServerEntry> serverEntries,
        ToolRegistry registry)
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
            NullLogger<McpClientManager>.Instance,
            new SessionConfig());
        return new McpSmokeHarness(manager, flowBroker);
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.StopAsync(CancellationToken.None);
        Manager.Dispose();
        _flowBroker.Dispose();
    }
}
