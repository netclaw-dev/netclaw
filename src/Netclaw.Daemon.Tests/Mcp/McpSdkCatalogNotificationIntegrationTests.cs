// -----------------------------------------------------------------------
// <copyright file="McpSdkCatalogNotificationIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

[Collection(McpSmokeChildProcessCollection.Name)]
public sealed class McpSdkCatalogNotificationIntegrationTests(ITestOutputHelper output)
{
    private static readonly McpServerName ServerName = new("notifications");

    [Fact]
    public async Task RawListenAcknowledgementAndToolEvent_RefreshLiveCatalog()
    {
        var entry = new McpServerEntry
        {
            Transport = "stdio",
            Command = "dotnet",
            Arguments = [SmokeMcpServerLocator.LocateDll(), "--catalog-notifications"],
            Enabled = true,
        };
        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { [ServerName.Value] = entry },
            registry,
            output);

        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        harness.AssertConnected(ServerName.Value);
        var first = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        var lease = Assert.IsType<McpCatalogNotificationLease>(first.NotificationLease);
        Assert.Equal(McpCatalogNotificationMode.Modern, lease.Mode);
        Assert.True(lease.ToolsEnabled);
        Assert.Contains("add-dynamic-tool", first.ToolFunctions.Keys);
        Assert.DoesNotContain("dynamic-added", first.ToolFunctions.Keys);

        var refreshed = lease.WaitForRefreshCompletionAsync(TestContext.Current.CancellationToken).AsTask();
        await first.Client!.CallToolAsync(
            "add-dynamic-tool",
            cancellationToken: TestContext.Current.CancellationToken);
        await refreshed;

        var second = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.Contains("dynamic-added", second.ToolFunctions.Keys);
    }

    [Fact]
    public async Task FailedStdioStartup_IsReportedBeforeLeaseAssertions()
    {
        // The GUID suffix on this command name is intentional and must stay random: it
        // regression-guards McpClientManager.FindHttpStatus against sniffing a status code
        // out of caller-supplied data. A prior bug made a bare Contains("401")/Contains("403")
        // check misclassify a random GUID substring (e.g. "632401b4...") as an HTTP auth
        // failure. See McpClientManagerStatusTests for the targeted unit coverage.
        var entry = new McpServerEntry
        {
            Transport = "stdio",
            Command = $"netclaw-missing-mcp-server-{Guid.NewGuid():N}",
            Enabled = true,
        };
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { [ServerName.Value] = entry },
            new ToolRegistry(),
            output);

        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(
            () => harness.AssertConnected(ServerName.Value));
        Assert.Contains("state=Unreachable", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Failed to reach MCP server", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Failed to connect to MCP server", output.Output, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException", output.Output, StringComparison.Ordinal);
        Assert.Contains(entry.Command, output.Output, StringComparison.Ordinal);
        Assert.Null(harness.Manager.GetSnapshot(ServerName)?.NotificationLease);
    }
}
