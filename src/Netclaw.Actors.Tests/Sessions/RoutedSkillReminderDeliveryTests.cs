// -----------------------------------------------------------------------
// <copyright file="RoutedSkillReminderDeliveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// S2: a reminder whose prompt is a routed slash command (<c>/ops-route ...</c>) runs the skill in a sub-agent and
/// records the turn in <c>HandleRoutedSkillExecutionCompleted</c>. The live session and a session that replays the
/// journal must treat that turn the same way. The spec <c>netclaw-session</c> says a routed-skill turn is an
/// ordinary recorded turn: the session finds the reminder and reports a failed post once.
/// </summary>
public sealed class RoutedSkillReminderDeliveryTests : ReminderDeliveryTestBase
{
    private const string RoutedPrompt = "/ops-route check daemon health";

    public RoutedSkillReminderDeliveryTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        base.ConfigureSessionServices(services);

        var skillDir = Path.Combine(TestPaths.BasePath, "routed-reminder-skill");
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile, """
            ---
            name: ops-route
            description: Route to operations helper.
            metadata:
              subagent: summarizer
            ---

            # Ops Route

            You specialize in daemon health checks.
            """);
        var skillRegistry = new SkillRegistry();
        skillRegistry.Register(new SkillEntry("ops-route", "Ops Route", "Route to operations helper.", skillFile, skillDir, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "summarizer"
        });
        services.AddSingleton(skillRegistry);

        var promptProvider = new StaticSystemPromptProvider("You are a test assistant.");
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        var toolAccessPolicy = new ToolAccessPolicy(new NetclawPaths(),
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));
        var subAgentRegistry = new SubAgentDefinitionRegistry();
        subAgentRegistry.Register(new SubAgentProfile
        {
            Name = "summarizer",
            Description = "Summarize content",
            SystemPrompt = "You are a summarizer.",
            ToolNames = ["file_read"],
            ModelRole = ModelRole.Compaction,
            Visibility = SubAgentVisibility.UserFacing,
            EmitStructuredFindings = false
        });

        var toolRegistry = new ToolRegistry();
        toolRegistry.RegisterCore(new StubFileReadTool());
        var spawner = new SubAgentSpawner(
            new SingleClientProvider(ChatClient),
            toolRegistry,
            toolAccessPolicy,
            approvalService: null,
            promptProvider,
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkingContextSnapshotProvider>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SubAgentSpawner>.Instance);

        services.AddSingleton(toolRegistry);
        services.AddSingleton(subAgentRegistry);
        services.AddSingleton(spawner);
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(
            toolRegistry,
            toolAccessPolicy,
            approvalService: null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatchingToolExecutor>.Instance));
    }

    [Fact]
    public async Task A_failed_post_on_a_routed_skill_reminder_turn_raises_one_alert()
    {
        // Arrange: the reminder prompt runs a routed skill. The turn is recorded by the routed-skill path.
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-live");
        var (subscriber, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey), RoutedPrompt);
        Assert.Equal(new Netclaw.Actors.Reminders.ReminderId(FireKey), completed.SourceReminderId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, subscriber);
        Assert.Equal(ReminderSlug, alert.Source);
    }

    [Fact]
    public async Task A_recovered_session_treats_a_routed_skill_reminder_turn_like_the_live_session()
    {
        // Arrange: the live session above raises one alert for this turn. A session that replays the journal
        // must raise the same single alert, not more and not fewer.
        var sessionId = new SessionId("mattermost/chan1/routed-reminder-recovered");
        var (_, completed) = await RunTurnAsync(sessionId, ReminderSource(FireKey), RoutedPrompt);
        await StopSessionAsync(sessionId);

        var recovered = CreateTestProbe("routed-recovered-subscriber");
        await JoinAsync(ActorRegistry.Get<SessionManagerActorKey>(), recovered, sessionId);

        // Act
        SendDeliveryFailed(sessionId, completed.TurnNumber, DeliveryFailureKind.TransportFailure);

        // Assert
        var alert = await AwaitExactlyOneAlertAsync(sessionId, recovered);
        Assert.Equal(ReminderSlug, alert.Source);
    }

    // The sub-agent profile needs one tool that the audience policy exposes. The model never calls it.
    private sealed class StubFileReadTool : INetclawTool
    {
        public string Name => "file_read";
        public LlmFacingToolName LlmFacingName { get; } = LlmFacingToolName.FromCanonical("file_read");
        public string Description => "Stub file read";
        public string GrantCategory => "file";
        public System.Text.Json.JsonElement ParameterSchema => default;

        public AITool ToAITool() => AIFunctionFactory.Create(() => "stub", name: Name, description: Description);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => Task.FromResult("stub");

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
            => Task.FromResult("stub");
    }
}
