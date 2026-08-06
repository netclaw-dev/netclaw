// -----------------------------------------------------------------------
// <copyright file="McpCatalogRefreshTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpCatalogRefreshTests
{
    private static readonly McpServerName ServerName = new("test");
    private static readonly DateTimeOffset InitialTime = DateTimeOffset.Parse("2026-07-22T12:00:00Z");

    [Fact]
    public async Task CatalogChange_RepublishesSnapshotAndGeneration()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("old_tool"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        // Connect marked the catalog fresh; the throttle must elapse before a refresh.
        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ToolNames = ["old_tool", "new_tool"];
        Assert.True(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        var snapshot = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(2, snapshot.Generation);
        Assert.Equal(2, snapshot.ToolFunctions.Count);
        Assert.Equal(2, snapshot.Status.ToolCount);
        Assert.Equal(1, plan.RefreshCount);
        Assert.Equal(1, runtime.CreateCount); // no reconnect
        AssertPublishedTools(harness, "new_tool", "old_tool");
    }

    [Fact]
    public async Task NoCatalogChange_DoesNotBumpGeneration()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("stable_tool"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        var snapshot = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(1, snapshot.Generation);
        Assert.Equal(1, plan.RefreshCount);
        Assert.Equal(1, runtime.CreateCount);
        AssertPublishedTools(harness, "stable_tool");
    }

    [Fact]
    public async Task RefreshIsThrottledWithinInterval()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        // First refresh immediately after connect is throttled (connect marked it fresh).
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ToolNames = ["tool_a", "tool_b"];
        Assert.True(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        // Second refresh within the interval is throttled even though the catalog changed.
        plan.ToolNames = ["tool_a", "tool_b", "tool_c"];
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        var snapshot = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(2, snapshot.Generation);
        Assert.Equal(1, plan.RefreshCount); // only the middle call actually re-listed
    }

    [Fact]
    public async Task FailedRefresh_KeepsLastGoodCatalogAndGeneration()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("old_tool"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ListFailure = new InvalidOperationException("server blew up");
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(1, plan.RefreshCount); // prove the failure path actually ran

        var snapshot = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(1, snapshot.Generation);
        Assert.Equal("old_tool", Assert.Single(snapshot.ToolFunctions).Key);
        Assert.Equal(McpConnectionState.Connected, snapshot.Status.State);
        AssertPublishedTools(harness, "old_tool");
    }

    [Fact]
    public async Task FailedRefresh_RollsBackThrottleSoNextTickRetries()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ListFailure = new InvalidOperationException("transient");
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(1, plan.RefreshCount);

        // The claim was rolled back, so a 30s advance allows an immediate retry rather
        // than forcing a 5-minute wait. Catalog is unchanged, so no generation bump.
        plan.ListFailure = null;
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(2, plan.RefreshCount);
        Assert.Equal(1, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Fact]
    public async Task EmptyCatalogRefresh_KeepsLastGoodTools()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a", "tool_b"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ToolNames = []; // server now reports no tools
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        var snapshot = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(1, snapshot.Generation);
        Assert.Equal(2, snapshot.ToolFunctions.Count);
        Assert.Equal(McpConnectionState.Connected, snapshot.Status.State);
        AssertPublishedTools(harness, "tool_a", "tool_b");
    }

    [Fact]
    public async Task EmptyCatalogRefresh_RollsBackThrottleSoNextTickRetries()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a", "tool_b"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ToolNames = []; // server now reports no tools
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(1, plan.RefreshCount);

        // The last-good, previously non-empty catalog stays published.
        var snapshotAfterEmpty = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(1, snapshotAfterEmpty.Generation);
        Assert.Equal(2, snapshotAfterEmpty.ToolFunctions.Count);
        AssertPublishedTools(harness, "tool_a", "tool_b");

        // The claim was rolled back, so a 30s advance allows an immediate retry rather
        // than forcing a 5-minute wait. The server recovers with a changed catalog, so
        // the re-list runs and the snapshot generation bumps.
        plan.ToolNames = ["tool_a", "tool_b", "tool_c"];
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(2, plan.RefreshCount);

        var snapshotAfterRecovery = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(2, snapshotAfterRecovery.Generation);
        Assert.Equal(3, snapshotAfterRecovery.ToolFunctions.Count);
        AssertPublishedTools(harness, "tool_a", "tool_b", "tool_c");
    }

    [Fact]
    public async Task RefreshOnUnknownServer_IsNoOp()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a"));
        await using var harness = CreateHarness(runtime);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(await harness.Manager.TryRefreshCatalogAsync(new McpServerName("missing"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Fingerprint_IgnoresToolOrder()
    {
        var a = AIFunctionFactory.Create(() => "unused", name: "tool_a", description: "desc");
        var b = AIFunctionFactory.Create(() => "unused", name: "tool_b", description: "desc");

        Assert.Equal(
            McpClientManager.ComputeCatalogFingerprint([a, b]),
            McpClientManager.ComputeCatalogFingerprint([b, a]));
    }

    [Fact]
    public void Fingerprint_ChangesOnDescriptionOrToolAdd()
    {
        var baseline = AIFunctionFactory.Create(() => "unused", name: "tool", description: "desc");
        var newDescription = AIFunctionFactory.Create(() => "unused", name: "tool", description: "changed");
        var addedTool = AIFunctionFactory.Create(() => "unused", name: "other", description: "desc");

        var baselineHash = McpClientManager.ComputeCatalogFingerprint([baseline]);
        Assert.NotEqual(baselineHash, McpClientManager.ComputeCatalogFingerprint([newDescription]));
        Assert.NotEqual(baselineHash, McpClientManager.ComputeCatalogFingerprint([baseline, addedTool]));
    }

    [Fact]
    public void CanonicalSchema_IgnoresKeyOrderAndWhitespace()
    {
        var a = JsonDocument.Parse("""{"z":1,"a":{"y":true,"b":"x"}}""").RootElement;
        var b = JsonDocument.Parse(""" { "a": { "b": "x", "y": true }, "z": 1 } """).RootElement;

        Assert.Equal(McpClientManager.CanonicalSchema(a), McpClientManager.CanonicalSchema(b));
    }

    [Fact]
    public void CanonicalSchema_ChangesOnSchemaEdit()
    {
        var a = JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"number"}}}""").RootElement;
        var b = JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"string"}}}""").RootElement;

        Assert.NotEqual(McpClientManager.CanonicalSchema(a), McpClientManager.CanonicalSchema(b));
    }

    private static void AssertPublishedTools(McpClientManagerLifecycleTests.ManagerHarness harness, params string[] expected)
    {
        Assert.Equal(expected, harness.Manager.GetToolNames(ServerName));
        Assert.Equal(expected.Length, harness.Manager.GetServerStatuses()[ServerName].ToolCount);
    }

    private static McpClientManagerLifecycleTests.ManagerHarness CreateHarness(McpClientManagerLifecycleTests.ControlledMcpClientRuntime runtime)
        => new(runtime, new FakeTimeProvider(InitialTime));

    private static McpClientManagerLifecycleTests.ManagerHarness CreateHarness(
        McpClientManagerLifecycleTests.ControlledMcpClientRuntime runtime,
        FakeTimeProvider time)
        => new(runtime, time);
}
