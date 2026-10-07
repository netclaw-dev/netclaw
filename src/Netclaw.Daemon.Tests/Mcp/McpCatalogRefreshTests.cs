// -----------------------------------------------------------------------
// <copyright file="McpCatalogRefreshTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
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
    public async Task FailedRefresh_FirstRetryIsOneTickLater()
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

        // The first backoff step is one 30s tick, so the retry comes well before
        // the 5-minute healthy interval. Catalog is unchanged, so no generation bump.
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
    public async Task EmptyCatalogRefresh_FirstRetryIsOneTickLater()
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

        // The first backoff step is one 30s tick, so the retry comes well before
        // the 5-minute healthy interval. The server recovers with a changed catalog, so
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
    public async Task FailingRefresh_BacksOffExponentiallyAtThirtySecondPolls()
    {
        // Issue #2261: a server that kept timing out was re-listed on every 30s tick.
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var time = new FakeTimeProvider(InitialTime);
        var attempts = new List<TimeSpan>();
        DateTimeOffset firstPollAt = default;
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a")
        {
            BeforeListTools = (_, _) =>
            {
                attempts.Add(time.GetUtcNow() - firstPollAt);
                return Task.CompletedTask;
            },
        });
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        firstPollAt = time.GetUtcNow();
        plan.ListFailure = new HttpRequestException("connection refused");

        // Ten minutes of polls at the reconnection service's 30s tick interval.
        var tick = McpReconnectionService.TickInterval;
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromMinutes(10); elapsed += tick)
        {
            await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken);
            time.Advance(tick);
        }

        // 30s, 60s, 120s, 240s gaps, then capped at 300s: five attempts instead of twenty.
        Assert.Equal(
            new[] { 0, 30, 90, 210, 450 }.Select(s => TimeSpan.FromSeconds(s)),
            attempts);
        Assert.Equal(5, harness.Manager.GetServerStatuses()[ServerName].ConsecutiveCatalogRefreshFailures);
        AssertPublishedTools(harness, "tool_a");
    }

    [Fact]
    public async Task RepeatedRefreshFailures_ReportDegradedOnce_AndUnchangedRefreshRestoresHealthyCadence()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ListFailure = new HttpRequestException("connection refused");
        const int threshold = McpClientManager.CatalogRefreshDegradedThreshold;
        for (var failures = 1; failures <= threshold; failures++)
        {
            var status = harness.Manager.GetServerStatuses()[ServerName];
            Assert.False(status.IsDegraded);
            Assert.Equal("healthy", DaemonRuntimeStatusService.ToConnector(ServerName, status).Status);
            await FailNextRefreshAsync(harness, plan, time, failures);
        }

        var degraded = harness.Manager.GetServerStatuses()[ServerName];
        Assert.True(degraded.IsDegraded);
        Assert.Equal(McpConnectionState.Connected, degraded.State);
        Assert.Contains("connection refused", degraded.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("degraded", DaemonRuntimeStatusService.ToConnector(ServerName, degraded).Status);
        // The cached tools stay published; the status only stops claiming health.
        AssertPublishedTools(harness, "tool_a");

        // Further failures keep it degraded without repeating the transition warning.
        var lastFailureAt = time.GetUtcNow();
        await FailNextRefreshAsync(harness, plan, time, threshold + 1);
        Assert.Single(harness.Logger.Entries, entry => entry.Contains("reported degraded", StringComparison.Ordinal));

        plan.ListFailure = null;
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(threshold + 2, plan.RefreshCount);

        var recovered = harness.Manager.GetServerStatuses()[ServerName];
        Assert.False(recovered.IsDegraded);
        Assert.Equal(0, recovered.ConsecutiveCatalogRefreshFailures);
        Assert.Null(recovered.ErrorMessage);
        // The last refresh failure stays the reported last error after recovery.
        Assert.Equal(lastFailureAt, recovered.LastErrorAt);
        Assert.Equal("healthy", DaemonRuntimeStatusService.ToConnector(ServerName, recovered).Status);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Contains($"recovered after {threshold + 1} consecutive failure(s)", StringComparison.Ordinal));

        // Backoff is cleared: the next poll waits the full healthy interval again.
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(threshold + 2, plan.RefreshCount);
        time.Advance(McpClientManager.CatalogRefreshInterval);
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(threshold + 3, plan.RefreshCount);
    }

    [Fact]
    public async Task DegradedServer_RecoversWhenTheSuccessfulRefreshChangesTheCatalog()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ListFailure = new HttpRequestException("connection refused");
        for (var failures = 1; failures <= McpClientManager.CatalogRefreshDegradedThreshold; failures++)
            await FailNextRefreshAsync(harness, plan, time, failures);
        Assert.True(harness.Manager.GetServerStatuses()[ServerName].IsDegraded);

        plan.ListFailure = null;
        plan.ToolNames = ["tool_a", "tool_b"];
        Assert.True(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        var recovered = harness.Manager.GetServerStatuses()[ServerName];
        Assert.False(recovered.IsDegraded);
        Assert.Equal(0, recovered.ConsecutiveCatalogRefreshFailures);
        Assert.Equal("healthy", DaemonRuntimeStatusService.ToConnector(ServerName, recovered).Status);
        Assert.Equal(2, harness.Manager.GetSnapshot(ServerName)?.Generation);
        AssertPublishedTools(harness, "tool_a", "tool_b");
    }

    [Fact]
    public async Task CallerCancelledRefresh_IsNotCountedAsAFailure()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a")
        {
            BeforeListTools = async (refreshCount, cancellationToken) =>
            {
                if (refreshCount != 1)
                    return;
                entered.TrySetResult();
                await never.Task.WaitAsync(cancellationToken); // only the first attempt hangs
            },
        });
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var refresh = harness.Manager.TryRefreshCatalogAsync(ServerName, caller.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await caller.CancelAsync();

        Assert.False(await refresh);
        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(0, status.ConsecutiveCatalogRefreshFailures);
        Assert.Null(status.ErrorMessage);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Contains("catalog refresh failed", StringComparison.Ordinal));

        // The abandoned attempt released its poll slot, so the next poll re-lists at once.
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(2, plan.RefreshCount);
    }

    [Fact]
    public void RefreshFailureOverlay_KeepsAnErrorAlreadyOnTheStatus()
    {
        var failedToolCallAt = InitialTime;
        var status = new McpServerStatus(
            ServerName, McpConnectionState.Connected, 1, "Tool call failed: HTTP 502", failedToolCallAt);
        var health = new McpCatalogRefreshHealth(
            McpClientManager.CatalogRefreshDegradedThreshold, "timed out after 15s", InitialTime.AddMinutes(2));

        var overlaid = McpClientManager.WithCatalogRefreshHealth(status, health);

        Assert.True(overlaid.IsDegraded);
        Assert.Equal("Tool call failed: HTTP 502", overlaid.ErrorMessage);
        Assert.Equal(InitialTime.AddMinutes(2), overlaid.LastErrorAt);
    }

    [Fact]
    public async Task HungRefresh_TimesOutOnTheTimeProvider_AndLogsOneLineWarning()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a")
        {
            BeforeListTools = async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await never.Task.WaitAsync(cancellationToken); // the server never answers
            },
        });
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        var refresh = harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(McpClientManager.CatalogRefreshTimeout);

        Assert.False(await refresh);
        Assert.Equal(1, harness.Manager.GetServerStatuses()[ServerName].ConsecutiveCatalogRefreshFailures);
        // An expected timeout is a single line: no exception (and so no stack trace) attached.
        Assert.Empty(harness.Logger.Exceptions);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Contains("catalog refresh failed: timed out after 15s", StringComparison.Ordinal));
        AssertPublishedTools(harness, "tool_a");
    }

    [Fact]
    public async Task UnexpectedRefreshFailure_KeepsItsStackTrace()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        var failure = new InvalidOperationException("server blew up");
        plan.ListFailure = failure;
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        Assert.Same(failure, Assert.Single(harness.Logger.Exceptions));
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

    [Fact]
    public async Task AuthorizationFailureDuringRefresh_MarksAwaitingAuthAndStopsTheRefreshLoop()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ListFailure = new ModelContextProtocol.McpException(
            "Failed to handle unauthorized response with 'Bearer' scheme. " +
            "The AuthorizationCallbackHandler returned a null authorization result.");
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        // The status must stop claiming a working connection, but the catalog stays
        // visible so the operator can see which server needs reauthorization.
        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.AwaitingAuth, status.State);
        Assert.Equal(1, status.ToolCount);
        AssertPublishedTools(harness, "tool_a");

        // The demoted status removes the server from the Connected refresh path:
        // no further listing attempts while the token is known-dead.
        time.Advance(McpClientManager.CatalogRefreshInterval);
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(1, plan.RefreshCount);
    }

    [Fact]
    public async Task StuckAuthorizationFlowDuringRefresh_MarksAwaitingAuth()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ListFailure = new McpOAuthAuthorizationInProgressException(ServerName);
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        Assert.Equal(McpConnectionState.AwaitingAuth, harness.Manager.GetServerStatuses()[ServerName].State);
        AssertPublishedTools(harness, "tool_a");
    }

    [Fact]
    public async Task AuthorizationFailureDuringRefreshWithoutStoredTokens_MarksAuthFailed()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(new McpClientManagerLifecycleTests.ClientPlan("tool_a"));
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = new McpClientManagerLifecycleTests.ManagerHarness(
            runtime,
            time,
            NullNotificationSink.Instance,
            McpClientManagerLifecycleTests.HttpEntry());
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        plan.ListFailure = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));

        // Netclaw holds no tokens for this server, so AwaitingAuth would name a remedy the
        // operator cannot run.
        var status = harness.Manager.GetServerStatuses()[ServerName];
        Assert.Equal(McpConnectionState.AuthFailed, status.State);
        Assert.Contains("credentials or headers", status.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("netclaw mcp auth", status.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        // The catalog stays visible so the operator sees which server needs the credential.
        AssertPublishedTools(harness, "tool_a");
    }

    private static async Task FailNextRefreshAsync(
        McpClientManagerLifecycleTests.ManagerHarness harness,
        McpClientManagerLifecycleTests.ClientPlan plan,
        FakeTimeProvider time,
        int expectedFailures)
    {
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(expectedFailures, harness.Manager.GetServerStatuses()[ServerName].ConsecutiveCatalogRefreshFailures);
        Assert.Equal(expectedFailures, plan.RefreshCount); // each failure is a real re-list attempt
        time.Advance(TimeSpan.FromMilliseconds(McpClientManager.ComputeCatalogRefreshIntervalMs(expectedFailures)));
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
