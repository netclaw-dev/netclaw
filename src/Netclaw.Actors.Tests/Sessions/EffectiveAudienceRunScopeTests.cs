// -----------------------------------------------------------------------
// <copyright file="EffectiveAudienceRunScopeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: the deployment posture narrows the recorded source audience. Every enforcement point
/// of a turn uses the narrowed (effective) audience, not only the tool exposure of the parent model.
/// The points are the parent run scope, the gate for a tool that the model calls by its registry name,
/// the tool set and the run scope of a spawned child, and the run scope of a routed skill.
/// </summary>
public sealed class EffectiveAudienceRunScopeTests : LlmSessionTestBase
{
    private const string ChildProfile = "probe-child";
    private const string RoutedSkill = "probe-route";
    private const string SetWebhook = ToolAudienceProfileToolCatalog.SetWebhook;

    private readonly FakeChatClient _parentModel = new();
    private readonly FakeChatClient _childModel = new();
    private readonly RecordingTool _parentProbe = new("parent_probe");
    private readonly RecordingTool _childProbe = new("child_probe");
    private readonly RecordingTool _webhook = new(SetWebhook);
    private DeploymentPosture? _posture;

    public EffectiveAudienceRunScopeTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        var prompts = new StaticPromptProvider();
        var models = new RoleModels(_parentModel, _childModel);
        services.AddSingleton<IChatClientProvider>(models);
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning { SnapshotInterval = 0, TitleGenerationInterval = 0 }
        });
        services.AddSingleton<ISystemPromptProvider>(prompts);

        // The session resolves the deriver when it starts, after the test row sets the posture.
        services.AddSingleton(_ => new TrustContextDeriver(new EffectivePolicyDefaults(
            _posture ?? throw new InvalidOperationException("The test row must set the posture first."),
            SecurityPolicyDefaults.ResolveAudience(_posture.Value),
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false)));

        // The tool policy reads only the shell mode from its defaults. The deriver above applies the posture.
        var policy = new ToolAccessPolicy(TestPaths, new ToolConfig(),
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var subAgents = new SubAgentDefinitionRegistry();
        subAgents.Register(new SubAgentProfile
        {
            Name = ChildProfile,
            Description = "Report the audience of the child run.",
            SystemPrompt = "You are a probe.",
            ToolNames = [_childProbe.Name, SetWebhook],
            ModelRole = ModelRole.Compaction,
            Visibility = SubAgentVisibility.UserFacing,
            EmitStructuredFindings = false
        });
        var registry = new ToolRegistry();
        var spawner = new SubAgentSpawner(models, registry, policy, approvalService: null, prompts,
            new WorkingContextSnapshotProvider(new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance, new SessionConfig());
        registry.RegisterCore(new SpawnAgentTool(subAgents, spawner, TestPaths));
        registry.RegisterCore(_parentProbe);
        registry.RegisterCore(_childProbe);
        registry.RegisterCore(_webhook);
        // spawn_agent starts a child only when the parent can read the child result with file_read.
        registry.RegisterCore(new RecordingTool(ToolAudienceProfileToolCatalog.FileRead));

        services.AddSingleton(registry);
        services.AddSingleton(policy);
        services.AddSingleton(subAgents);
        services.AddSingleton(spawner);
        services.AddSingleton(RoutedSkillRegistry());
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(
            registry, policy, approvalService: null, NullLogger<DispatchingToolExecutor>.Instance));
    }

    // spawnAllowed is false for Public: netclaw-subagents "Context layer subagent awareness" says that a
    // Public session cannot spawn a subagent through a tool call. An operator slash command is not a tool
    // call, so the routed child still runs. It inherits the narrowed audience ("Routed subagent inherits
    // launch audience").
    [Theory]
    [InlineData(DeploymentPosture.Team, TrustAudience.Personal, TrustAudience.Team, true)]
    [InlineData(DeploymentPosture.Public, TrustAudience.Personal, TrustAudience.Public, false)]
    [InlineData(DeploymentPosture.Personal, TrustAudience.Personal, TrustAudience.Personal, true)]
    [InlineData(DeploymentPosture.Team, TrustAudience.Team, TrustAudience.Team, true)]
    public async Task Every_enforcement_point_uses_the_effective_audience(
        DeploymentPosture posture,
        TrustAudience sourceAudience,
        TrustAudience effectiveAudience,
        bool spawnAllowed)
    {
        _posture = posture;

        // The parent calls set_webhook by its registry name, also when its exposure hides the tool.
        _parentModel.ToolCallsOnFirstCall =
        [
            Call("call-parent-probe", _parentProbe.Name),
            Call("call-webhook", SetWebhook),
            Call("call-spawn", "spawn_agent", ("agent", ChildProfile), ("task", "Report the audience."))
        ];
        _childModel.ResponseContentsSelector = messages =>
            messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any())
                ? null
                : [Call("call-child-probe", _childProbe.Name)];

        var spawned = await RunAsync(new SessionId("console/posture-spawn"), "Spawn the probe.", sourceAudience);
        var routed = await RunAsync(new SessionId("console/posture-routed"), $"/{RoutedSkill} go", sourceAudience);

        Assert.Equal(Expected(effectiveAudience, spawnAllowed), Observed(spawned, routed));
    }

    // One entry for each enforcement point. "refused" means that the tool refused the spawn and no child ran.
    private static string[] Expected(TrustAudience audience, bool spawnAllowed)
    {
        var personal = audience == TrustAudience.Personal;
        return
        [
            $"parent run scope={audience}",
            $"set_webhook by name ran={personal}",
            $"spawned child has set_webhook={(spawnAllowed ? personal.ToString() : "refused")}",
            $"routed child has set_webhook={personal}",
            $"spawned child run scope={(spawnAllowed ? audience.ToString() : "refused")}",
            $"routed child run scope={audience}"
        ];
    }

    private string[] Observed(TurnObservation spawned, TurnObservation routed)
    {
        return
        [
            $"parent run scope={Assert.Single(_parentProbe.Audiences)}",
            $"set_webhook by name ran={!_webhook.Audiences.IsEmpty}",
            $"spawned child has set_webhook={spawned.ChildHasSetWebhook}",
            $"routed child has set_webhook={routed.ChildHasSetWebhook}",
            $"spawned child run scope={spawned.ChildAudience}",
            $"routed child run scope={routed.ChildAudience}"
        ];
    }

    // Runs one turn. It returns after the turn completes and after the child of the turn completes, if a
    // child started. A turn starts at most one child.
    private async Task<TurnObservation> RunAsync(SessionId sessionId, string content, TrustAudience audience)
    {
        var ct = TestContext.Current.CancellationToken;
        var childCallsBefore = _childModel.ReceivedMessages.Count;
        var childAudiencesBefore = _childProbe.Audiences.Count;
        var subscriber = CreateTestProbe();
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        await JoinSessionAsync(sessionManager, subscriber, sessionId, OutputFilter.Full);
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = new MessageSource
            {
                ChannelType = ChannelType.SignalR,
                SenderId = new SenderId("operator-1"),
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                Principal = PrincipalClassification.Operator,
                Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
                ReceivedAt = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)
            }
        }, TimeSpan.FromSeconds(5), ct);

        bool childStarted = false, childDone = false, turnDone = false, spawnRefused = false;
        while (!turnDone || childStarted != childDone)
        {
            var output = await subscriber.ExpectMsgAsync<object>(TimeSpan.FromSeconds(15), cancellationToken: ct);
            childStarted |= output is SubAgentOutput { Phase: SubAgentPhase.Started };
            childDone |= output is SubAgentOutput { Phase: SubAgentPhase.Completed };
            turnDone |= output is TurnCompleted;
            spawnRefused |= output is ToolResultOutput result
                && result.ToolName.Value == "spawn_agent"
                && result.Result.Contains("This tool is not available.", StringComparison.Ordinal);
        }

        if (!childStarted)
        {
            var state = spawnRefused ? "refused" : "no child and no refusal";
            return new TurnObservation(state, state);
        }

        // The first model call of the child has no tool results. It shows the tools that the child got.
        var firstChildCall = _childModel.ReceivedMessages
            .Select((messages, index) => (messages, index))
            .Skip(childCallsBefore)
            .First(call => !call.messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any()));
        var childAudience = Assert.Single(_childProbe.Audiences.Skip(childAudiencesBefore));
        return new TurnObservation(
            _childModel.ReceivedToolNames[firstChildCall.index].Contains(SetWebhook).ToString(),
            childAudience.ToString());
    }

    private sealed record TurnObservation(string ChildHasSetWebhook, string ChildAudience);

    private static FunctionCallContent Call(string callId, string name, params (string Key, object? Value)[] arguments)
    {
        var values = arguments.ToDictionary(argument => argument.Key, argument => argument.Value, StringComparer.Ordinal);
        values["_rationale"] = "Probe the audience of the run.";
        return new FunctionCallContent(callId, name, values);
    }

    private SkillRegistry RoutedSkillRegistry()
    {
        var skillDirectory = Path.Join(TestPaths.BasePath, "skill-routing", RoutedSkill);
        Directory.CreateDirectory(skillDirectory);
        var skillFile = Path.Join(skillDirectory, "SKILL.md");
        File.WriteAllText(skillFile, $"""
            ---
            name: {RoutedSkill}
            description: Route to the probe child.
            metadata:
              subagent: {ChildProfile}
            ---

            # Probe Route
            """);
        var skills = new SkillRegistry();
        skills.Register(new SkillEntry(RoutedSkill, "Probe Route", "Route to the probe child.", skillFile, skillDirectory, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = ChildProfile
        });
        return skills;
    }

    private sealed class RoleModels(IChatClient parent, IChatClient child) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => role == ModelRole.Compaction ? child : parent;
    }

    private sealed class StaticPromptProvider : ISystemPromptProvider
    {
        public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null) => "You are a test assistant.";

        public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory) => null;

        public string? GetOperatingRules(TrustAudience audience) => null;
    }

    private sealed class RecordingTool(string name) : INetclawTool
    {
        public ConcurrentQueue<TrustAudience> Audiences { get; } = new();
        public string Name { get; } = name;
        public LlmFacingToolName LlmFacingName { get; } = LlmFacingToolName.FromCanonical(name);
        public string Description => "Records the audience of the run.";
        public string GrantCategory => "builtin";
        public System.Text.Json.JsonElement ParameterSchema => default;

        public AITool ToAITool() => AIFunctionFactory.Create(() => "ok", name: Name, description: Description);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => throw new InvalidOperationException("The probe needs a run scope.");

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
        {
            Audiences.Enqueue(context.Audience);
            return Task.FromResult("ok");
        }
    }
}
