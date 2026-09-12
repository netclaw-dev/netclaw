// -----------------------------------------------------------------------
// <copyright file="McpCatalogNotificationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpCatalogNotificationTests
{
    private static readonly McpServerName ServerName = new("test");
    private static readonly DateTimeOffset InitialTime = DateTimeOffset.Parse("2026-08-10T12:00:00Z");

    [Theory]
    [InlineData(NotificationMethods.ToolListChangedNotification)]
    [InlineData(NotificationMethods.PromptListChangedNotification)]
    public async Task ModernCatalogNotification_RefreshesCompleteCatalogWithoutPollDelay(string notificationMethod)
    {
        var plan = CreateModernPlan("old_tool");
        plan.Prompts = [CreatePrompt("old description")];
        await using var harness = CreateHarness(plan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, plan.ListenCount);
        Assert.Equal(McpCatalogNotificationLease.SubscriptionId, plan.ListenRequestId.Id);
        Assert.True(plan.ListenNotifications?.ToolsListChanged);
        Assert.True(plan.ListenNotifications?.PromptsListChanged);

        var lease = GetLease(harness);
        plan.ToolNames = ["old_tool", "new_tool"];
        plan.Prompts = [CreatePrompt("new description")];
        await NotifyAndWaitForRefreshAsync(plan, lease, notificationMethod, ModernParameters());

        var snapshot = Assert.IsType<McpServerSnapshot>(harness.Manager.GetSnapshot(ServerName));
        Assert.Equal(2, snapshot.Generation);
        Assert.Equal(["new_tool", "old_tool"], harness.Manager.GetToolNames(ServerName));
        Assert.Equal("new description", snapshot.PromptDescriptors["workflow"].Description);
        Assert.Equal(1, plan.RefreshCount);
    }

    [Fact]
    public async Task ModernPartialAcknowledgement_EnablesOnlyAcceptedEvent()
    {
        var plan = CreateModernPlan(toolsAccepted: true, promptsAccepted: false, "tool");
        await using var harness = CreateHarness(plan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var lease = GetLease(harness);
        Assert.True(lease.ToolsEnabled);
        Assert.False(lease.PromptsEnabled);

        await plan.NotifyAsync(
            NotificationMethods.PromptListChangedNotification,
            ModernParameters(),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, plan.RefreshCount);

        await NotifyAndWaitForRefreshAsync(
            plan,
            lease,
            NotificationMethods.ToolListChangedNotification,
            ModernParameters());
        Assert.Equal(1, plan.RefreshCount);
    }

    [Fact]
    public async Task LegacyDirectNotification_RefreshesWithoutListenRequest()
    {
        var plan = new McpClientManagerLifecycleTests.ClientPlan("old_tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2025-11-25", true, false),
        };
        await using var harness = CreateHarness(plan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, plan.ListenCount);
        var lease = GetLease(harness);
        plan.ToolNames = ["new_tool"];
        await NotifyAndWaitForRefreshAsync(
            plan,
            lease,
            NotificationMethods.ToolListChangedNotification,
            new JsonObject());

        Assert.Equal(["new_tool"], harness.Manager.GetToolNames(ServerName));
        Assert.Equal(2, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Fact]
    public async Task UnsupportedLegacyNotifications_UsePollRepair()
    {
        var plan = new McpClientManagerLifecycleTests.ClientPlan("old_tool");
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(time, plan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        plan.ToolNames = ["new_tool"];
        await plan.NotifyAsync(
            NotificationMethods.ToolListChangedNotification,
            new JsonObject(),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, plan.RefreshCount);

        time.Advance(McpClientManager.CatalogRefreshInterval);
        Assert.True(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(["new_tool"], harness.Manager.GetToolNames(ServerName));
    }

    [Fact]
    public async Task NotificationBurst_RunsOneRefreshAndOneFollowUp()
    {
        var firstRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = new McpClientManagerLifecycleTests.ClientPlan("tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2025-11-25", true, true),
            BeforeListTools = async (count, cancellationToken) =>
            {
                if (count != 1)
                    return;
                firstRefreshStarted.TrySetResult();
                await releaseFirstRefresh.Task.WaitAsync(cancellationToken);
            },
        };
        await using var harness = CreateHarness(plan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var lease = GetLease(harness);
        var firstCompleted = lease.WaitForRefreshCompletionAsync(TestContext.Current.CancellationToken).AsTask();
        await plan.NotifyAsync(
            NotificationMethods.ToolListChangedNotification,
            new JsonObject(),
            TestContext.Current.CancellationToken);
        await firstRefreshStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var secondCompleted = lease.WaitForRefreshCompletionAsync(TestContext.Current.CancellationToken).AsTask();
        for (var index = 0; index < 20; index++)
            await plan.NotifyAsync(
                NotificationMethods.PromptListChangedNotification,
                new JsonObject(),
                TestContext.Current.CancellationToken);

        releaseFirstRefresh.TrySetResult();
        await firstCompleted;
        await secondCompleted;
        Assert.Equal(2, plan.RefreshCount);
        Assert.Equal(2, plan.PromptRefreshCount);
    }

    [Fact]
    public async Task PrePublicationNotification_IsProcessedAfterPublication()
    {
        McpClientManagerLifecycleTests.ClientPlan? plan = null;
        plan = new McpClientManagerLifecycleTests.ClientPlan("tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2025-11-25", true, false),
            Initialize = cancellationToken => plan!.NotifyAsync(
                NotificationMethods.ToolListChangedNotification,
                new JsonObject(),
                cancellationToken).AsTask(),
        };
        await using var harness = CreateHarness(plan);

        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        await GetLease(harness).WaitForRefreshCompletionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, plan.RefreshCount);
        Assert.Equal(1, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Fact]
    public async Task Reconnect_ReplacesLeaseAndIgnoresOldNotifications()
    {
        var oldPlan = new McpClientManagerLifecycleTests.ClientPlan("old_tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2025-11-25", true, false),
        };
        var newPlan = new McpClientManagerLifecycleTests.ClientPlan("new_tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2025-11-25", true, false),
        };
        await using var harness = CreateHarness(oldPlan, newPlan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var oldLease = GetLease(harness);

        Assert.True(await harness.Manager.TryReconnectAsync(ServerName, TestContext.Current.CancellationToken));
        var newLease = GetLease(harness);
        Assert.NotSame(oldLease, newLease);
        Assert.Equal(1, oldPlan.DisposeCount);

        await oldPlan.NotifyAsync(
            NotificationMethods.ToolListChangedNotification,
            new JsonObject(),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, oldPlan.RefreshCount);

        newPlan.ToolNames = ["new_tool", "later_tool"];
        await NotifyAndWaitForRefreshAsync(
            newPlan,
            newLease,
            NotificationMethods.ToolListChangedNotification,
            new JsonObject());
        Assert.Equal(["later_tool", "new_tool"], harness.Manager.GetToolNames(ServerName));
    }

    [Fact]
    public async Task Shutdown_DisablesLeaseAndDisposesClient()
    {
        var plan = new McpClientManagerLifecycleTests.ClientPlan("tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2025-11-25", true, false),
        };
        await using var harness = CreateHarness(plan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var lease = GetLease(harness);

        await harness.Manager.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(McpCatalogNotificationMode.Disabled, lease.Mode);
        Assert.Equal(1, plan.DisposeCount);
        await plan.NotifyAsync(
            NotificationMethods.ToolListChangedNotification,
            new JsonObject(),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, plan.RefreshCount);
    }

    [Fact]
    public async Task FailedNotificationRefresh_KeepsLastGoodAndCanRetry()
    {
        var plan = new McpClientManagerLifecycleTests.ClientPlan("old_tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2025-11-25", true, false),
        };
        await using var harness = CreateHarness(plan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var lease = GetLease(harness);

        plan.ListFailure = new InvalidOperationException("controlled failure");
        await NotifyAndWaitForRefreshAsync(
            plan,
            lease,
            NotificationMethods.ToolListChangedNotification,
            new JsonObject());
        Assert.Equal(["old_tool"], harness.Manager.GetToolNames(ServerName));
        Assert.Equal(1, harness.Manager.GetSnapshot(ServerName)?.Generation);

        plan.ListFailure = null;
        plan.ToolNames = ["new_tool"];
        await NotifyAndWaitForRefreshAsync(
            plan,
            lease,
            NotificationMethods.ToolListChangedNotification,
            new JsonObject());
        Assert.Equal(["new_tool"], harness.Manager.GetToolNames(ServerName));
        Assert.Equal(2, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Fact]
    public async Task ModernListenFailure_KeepsConnectionAndLogsSafeCategory()
    {
        var plan = new McpClientManagerLifecycleTests.ClientPlan("tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2026-07-28", false, false),
            Listen = _ => Task.FromException(new McpException("secret response body")),
        };
        await using var harness = CreateHarness(plan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(McpConnectionState.Connected, harness.Manager.GetServerStatuses()[ServerName].State);
        Assert.Equal(McpCatalogNotificationMode.Disabled, GetLease(harness).Mode);
        Assert.Contains(harness.Logger.Entries, entry => entry.Contains("McpException", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Contains("secret response body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModernNullAcknowledgement_KeepsConnectionAndUsesPollRepair()
    {
        McpClientManagerLifecycleTests.ClientPlan? plan = null;
        var listenerLifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        plan = new McpClientManagerLifecycleTests.ClientPlan("old_tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2026-07-28", false, false),
            Listen = async cancellationToken =>
            {
                await plan!.NotifyAsync(
                    NotificationMethods.SubscriptionsAcknowledgedNotification,
                    new JsonObject
                    {
                        ["notifications"] = null,
                        ["_meta"] = SubscriptionMetadata(),
                    },
                    cancellationToken);
                await listenerLifetime.Task.WaitAsync(cancellationToken);
            },
        };
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(time, plan);

        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(McpConnectionState.Connected, harness.Manager.GetServerStatuses()[ServerName].State);
        Assert.Equal(McpCatalogNotificationMode.Disabled, GetLease(harness).Mode);
        Assert.Equal(["old_tool"], harness.Manager.GetToolNames(ServerName));
        Assert.Contains(harness.Logger.Entries, entry => entry.Contains("invalid", StringComparison.Ordinal));

        plan.ToolNames = ["new_tool"];
        time.Advance(McpClientManager.CatalogRefreshInterval);
        Assert.True(await harness.Manager.TryRefreshCatalogAsync(ServerName, TestContext.Current.CancellationToken));
        Assert.Equal(["new_tool"], harness.Manager.GetToolNames(ServerName));
    }

    [Fact]
    public async Task ModernAcknowledgementTimeout_UsesTimeProviderAndKeepsConnection()
    {
        var listenerLifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = new McpClientManagerLifecycleTests.ClientPlan("tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2026-07-28", false, false),
            Listen = cancellationToken => listenerLifetime.Task.WaitAsync(cancellationToken),
        };
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = CreateHarness(time, plan);

        var start = harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        await plan.ListenStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(McpCatalogNotificationLease.AcknowledgementTimeout);
        await start;

        Assert.Equal(McpConnectionState.Connected, harness.Manager.GetServerStatuses()[ServerName].State);
        Assert.Equal(McpCatalogNotificationMode.Disabled, GetLease(harness).Mode);
        Assert.Contains(harness.Logger.Entries, entry => entry.Contains("did not acknowledge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModernListenerClosure_DisablesLeaseAndKeepsConnection()
    {
        McpClientManagerLifecycleTests.ClientPlan? plan = null;
        var closeListener = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        plan = new McpClientManagerLifecycleTests.ClientPlan("tool")
        {
            NotificationProfile = new McpCatalogNotificationProfile("2026-07-28", false, false),
            Listen = async cancellationToken =>
            {
                await plan!.NotifyAsync(
                    NotificationMethods.SubscriptionsAcknowledgedNotification,
                    ModernAcknowledgement(tools: true, prompts: true),
                    cancellationToken);
                await closeListener.Task.WaitAsync(cancellationToken);
            },
        };
        await using var harness = CreateHarness(plan);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var lease = GetLease(harness);
        var stopped = lease.WaitForListenerStopAsync(TestContext.Current.CancellationToken);

        closeListener.TrySetResult();
        await stopped;

        Assert.Equal(McpCatalogNotificationMode.Disabled, lease.Mode);
        Assert.Equal(McpConnectionState.Connected, harness.Manager.GetServerStatuses()[ServerName].State);
    }

    private static McpClientManagerLifecycleTests.ClientPlan CreateModernPlan(params string[] toolNames)
        => CreateModernPlan(true, true, toolNames);

    private static McpClientManagerLifecycleTests.ClientPlan CreateModernPlan(
        bool toolsAccepted,
        bool promptsAccepted,
        params string[] toolNames)
    {
        McpClientManagerLifecycleTests.ClientPlan? plan = null;
        var listenerLifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        plan = new McpClientManagerLifecycleTests.ClientPlan(toolNames)
        {
            NotificationProfile = new McpCatalogNotificationProfile("2026-07-28", false, false),
            Listen = async cancellationToken =>
            {
                await plan!.NotifyAsync(
                    NotificationMethods.SubscriptionsAcknowledgedNotification,
                    ModernAcknowledgement(toolsAccepted, promptsAccepted),
                    cancellationToken);
                await listenerLifetime.Task.WaitAsync(cancellationToken);
            },
        };
        return plan;
    }

    private static JsonObject ModernAcknowledgement(bool tools, bool prompts)
    {
        var parameters = JsonSerializer.SerializeToNode(
            new SubscriptionsAcknowledgedNotificationParams
            {
                Notifications = new SubscriptionsListenNotifications
                {
                    ToolsListChanged = tools,
                    PromptsListChanged = prompts,
                },
            },
            McpJsonUtilities.DefaultOptions)!.AsObject();
        parameters["_meta"] = SubscriptionMetadata();
        return parameters;
    }

    private static JsonObject ModernParameters()
        => new() { ["_meta"] = SubscriptionMetadata() };

    private static JsonObject SubscriptionMetadata()
        => new() { [MetaKeys.SubscriptionId] = McpCatalogNotificationLease.SubscriptionId };

    private static Prompt CreatePrompt(string description)
        => new()
        {
            Name = "workflow",
            Title = "Workflow",
            Description = description,
        };

    private static McpCatalogNotificationLease GetLease(
        McpClientManagerLifecycleTests.ManagerHarness harness)
        => Assert.IsType<McpCatalogNotificationLease>(harness.Manager.GetSnapshot(ServerName)?.NotificationLease);

    private static async Task NotifyAndWaitForRefreshAsync(
        McpClientManagerLifecycleTests.ClientPlan plan,
        McpCatalogNotificationLease lease,
        string notificationMethod,
        JsonNode? parameters)
    {
        var refreshed = lease.WaitForRefreshCompletionAsync(TestContext.Current.CancellationToken).AsTask();
        await plan.NotifyAsync(notificationMethod, parameters, TestContext.Current.CancellationToken);
        await refreshed;
    }

    private static McpClientManagerLifecycleTests.ManagerHarness CreateHarness(
        params McpClientManagerLifecycleTests.ClientPlan[] plans)
        => CreateHarness(new FakeTimeProvider(InitialTime), plans);

    private static McpClientManagerLifecycleTests.ManagerHarness CreateHarness(
        FakeTimeProvider timeProvider,
        params McpClientManagerLifecycleTests.ClientPlan[] plans)
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        foreach (var plan in plans)
            runtime.Enqueue(plan);
        return new McpClientManagerLifecycleTests.ManagerHarness(runtime, timeProvider);
    }
}
