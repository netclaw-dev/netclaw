// -----------------------------------------------------------------------
// <copyright file="McpPromptSkillTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpPromptSkillTests
{
    private static readonly McpServerName ServerName = new("test");
    private static readonly DateTimeOffset InitialTime = DateTimeOffset.Parse("2026-07-22T12:00:00Z");

    public static TheoryData<IReadOnlyDictionary<string, string>?, string> InvalidArguments => new()
    {
        { null, "requires argument(s): property" },
        {
            new Dictionary<string, string>
            {
                ["property"] = "petabridge-com",
                ["unexpected"] = "value",
            },
            "unknown argument(s): unexpected"
        },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task LoadRejectsInvalidArguments(
        IReadOnlyDictionary<string, string>? arguments,
        string expectedError)
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        runtime.Enqueue(CreatePromptPlan());
        await using var harness = new McpClientManagerLifecycleTests.ManagerHarness(
            runtime,
            new FakeTimeProvider(InitialTime));
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var source = Assert.IsType<McpPromptSkillSource>(
            harness.SkillRegistry.GetByName("mcp__test__analyze-property")?.Source);

        var result = await harness.Manager.LoadAsync(
            source,
            arguments,
            TestToolExecutionContext.CreateUnbound(TrustAudience.Personal).Invocation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCallsPromptWithExactArgumentsAndPreservesRoles()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(CreatePromptPlan());
        await using var harness = new McpClientManagerLifecycleTests.ManagerHarness(
            runtime,
            new FakeTimeProvider(InitialTime));
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var source = Assert.IsType<McpPromptSkillSource>(
            harness.SkillRegistry.GetByName("mcp__test__analyze-property")?.Source);

        var result = await harness.Manager.LoadAsync(
            source,
            new Dictionary<string, string> { ["property"] = "petabridge-com" },
            TestToolExecutionContext.CreateUnbound(TrustAudience.Personal).Invocation,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("analyze-property", plan.LastPromptName);
        Assert.Equal("petabridge-com", plan.LastPromptArguments?["property"]);
        Assert.Collection(result.Messages,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("Inspect complete months.", message.Text);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("Use the live query endpoint.", message.Text);
            });
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public async Task LoadDeniedAudienceReturnsGenericErrorBeforePromptRequest(TrustAudience audience)
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(CreatePromptPlan());
        await using var harness = new McpClientManagerLifecycleTests.ManagerHarness(
            runtime,
            new FakeTimeProvider(InitialTime));
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var source = Assert.IsType<McpPromptSkillSource>(
            harness.SkillRegistry.GetByName("mcp__test__analyze-property")?.Source);

        var result = await harness.Manager.LoadAsync(
            source,
            new Dictionary<string, string> { ["property"] = "petabridge-com" },
            TestToolExecutionContext.CreateUnbound(audience).Invocation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("Error: This skill is not available.", result.Error);
        Assert.Equal(0, plan.PromptInvocationCount);
        Assert.Null(plan.LastPromptName);
    }

    [Theory]
    [InlineData("Summary", "summary")]
    [InlineData("Analyze-Property", "ANALYZE-PROPERTY")]
    public void PromptCatalogRejectsNamesThatCollideAfterCaseNormalization(
        string firstName,
        string secondName)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            McpClientManager.CreatePromptMap(
            [
                CreatePrompt("First prompt.", firstName),
                CreatePrompt("Second prompt.", secondName),
            ]));

        Assert.Contains(secondName, error.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptCatalogChangePublishesNewGenerationAndRejectsOldSource()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(CreatePromptPlan());
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = new McpClientManagerLifecycleTests.ManagerHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var oldSource = Assert.IsType<McpPromptSkillSource>(
            harness.SkillRegistry.GetByName("mcp__test__analyze-property")?.Source);

        plan.Prompts = [CreatePrompt("Analyze a property with revised guidance.")];
        time.Advance(McpClientManager.CatalogRefreshInterval);
        Assert.True(await harness.Manager.TryRefreshCatalogAsync(
            ServerName,
            TestContext.Current.CancellationToken));

        var newSource = Assert.IsType<McpPromptSkillSource>(
            harness.SkillRegistry.GetByName("mcp__test__analyze-property")?.Source);
        Assert.Equal(1, oldSource.Generation);
        Assert.Equal(2, newSource.Generation);
        Assert.Equal(1, plan.PromptRefreshCount);

        var result = await harness.Manager.LoadAsync(
            oldSource,
            new Dictionary<string, string> { ["property"] = "petabridge-com" },
            TestToolExecutionContext.CreateUnbound(TrustAudience.Personal).Invocation,
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Contains("stale generation 1", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptListFailureKeepsLastGoodGeneration()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(CreatePromptPlan());
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = new McpClientManagerLifecycleTests.ManagerHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        plan.PromptListFailure = new InvalidOperationException("prompt list failed");
        time.Advance(McpClientManager.CatalogRefreshInterval);
        Assert.False(await harness.Manager.TryRefreshCatalogAsync(
            ServerName,
            TestContext.Current.CancellationToken));

        Assert.Equal(1, harness.Manager.GetSnapshot(ServerName)?.Generation);
        Assert.NotNull(harness.SkillRegistry.GetByName("mcp__test__analyze-property"));
    }

    [Fact]
    public async Task EmptyPromptCatalogRemovesPromptSkillsWhileToolsRemain()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = runtime.Enqueue(CreatePromptPlan());
        var time = new FakeTimeProvider(InitialTime);
        await using var harness = new McpClientManagerLifecycleTests.ManagerHarness(runtime, time);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        plan.Prompts = [];
        time.Advance(McpClientManager.CatalogRefreshInterval);
        Assert.True(await harness.Manager.TryRefreshCatalogAsync(
            ServerName,
            TestContext.Current.CancellationToken));

        Assert.Null(harness.SkillRegistry.GetByName("mcp__test__analyze-property"));
        Assert.Equal(["query"], harness.Manager.GetToolNames(ServerName));
        Assert.Equal(2, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    [Fact]
    public async Task LoadRejectsUnsupportedPromptContent()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = CreatePromptPlan();
        plan.GetPromptResult = new GetPromptResult
        {
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png"),
                },
            ],
        };
        runtime.Enqueue(plan);
        await using var harness = new McpClientManagerLifecycleTests.ManagerHarness(
            runtime,
            new FakeTimeProvider(InitialTime));
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var source = Assert.IsType<McpPromptSkillSource>(
            harness.SkillRegistry.GetByName("mcp__test__analyze-property")?.Source);

        var result = await harness.Manager.LoadAsync(
            source,
            new Dictionary<string, string> { ["property"] = "petabridge-com" },
            TestToolExecutionContext.CreateUnbound(TrustAudience.Personal).Invocation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("unsupported content type 'image'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadReturnsFailedResultForAnApplicationHttpStatus()
    {
        var runtime = new McpClientManagerLifecycleTests.ControlledMcpClientRuntime();
        var plan = CreatePromptPlan();
        plan.GetPromptFailure = new HttpRequestException(
            "boom",
            null,
            HttpStatusCode.InternalServerError);
        runtime.Enqueue(plan);
        // A replacement is queued but must stay unused. Without it a reconnect would fail
        // inside the runtime before it counts the client, and CreateCount would still
        // read 1. With it, one reconnect drives CreateCount to 2 and this test fails.
        var replacement = runtime.Enqueue(CreatePromptPlan());
        await using var harness = new McpClientManagerLifecycleTests.ManagerHarness(
            runtime,
            new FakeTimeProvider(InitialTime));
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        var source = Assert.IsType<McpPromptSkillSource>(
            harness.SkillRegistry.GetByName("mcp__test__analyze-property")?.Source);

        var result = await harness.Manager.LoadAsync(
            source,
            new Dictionary<string, string> { ["property"] = "petabridge-com" },
            TestToolExecutionContext.CreateUnbound(TrustAudience.Personal).Invocation,
            TestContext.Current.CancellationToken);

        // The load owns this failure. An escaped exception would reach the tool dispatcher,
        // and a reconnect cannot change a status the server chose to send.
        Assert.False(result.Success);
        Assert.Contains("analyze-property", result.Error, StringComparison.Ordinal);
        // The first catch clause owns this text. The transport clause says "connection
        // closed" instead, so this pins which clause ran.
        Assert.Contains("failed:", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("connection closed", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, runtime.CreateCount);
        Assert.Null(replacement.Client);
        Assert.Equal(1, harness.Manager.GetSnapshot(ServerName)?.Generation);
    }

    private static McpClientManagerLifecycleTests.ClientPlan CreatePromptPlan()
        => new("query")
        {
            Prompts = [CreatePrompt("Analyze a property.")],
            GetPromptResult = new GetPromptResult
            {
                Description = "A rendered analytics workflow.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock { Text = "Inspect complete months." },
                    },
                    new PromptMessage
                    {
                        Role = Role.Assistant,
                        Content = new TextContentBlock { Text = "Use the live query endpoint." },
                    },
                ],
            },
        };

    private static Prompt CreatePrompt(string description, string name = "analyze-property")
        => new()
        {
            Name = name,
            Title = name,
            Description = description,
            Arguments =
            [
                new PromptArgument
                {
                    Name = "property",
                    Description = "The property identifier.",
                    Required = true,
                },
            ],
        };
}
