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

    [Theory]
    [InlineData(DeploymentPosture.Team, TrustAudience.Personal, TrustAudience.Team)]
    [InlineData(DeploymentPosture.Public, TrustAudience.Personal, TrustAudience.Public)]
    [InlineData(DeploymentPosture.Personal, TrustAudience.Personal, TrustAudience.Personal)]
    [InlineData(DeploymentPosture.Team, TrustAudience.Team, TrustAudience.Team)]
    public async Task Every_enforcement_point_uses_the_effective_audience(
        DeploymentPosture posture,
        TrustAudience sourceAudience,
        TrustAudience effectiveAudience)
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

        await RunAsync(new SessionId("console/posture-spawn"), "Spawn the probe.", sourceAudience);
        await RunAsync(new SessionId("console/posture-routed"), $"/{RoutedSkill} go", sourceAudience);

        Assert.Equal(Expected(effectiveAudience), Observed());
    }

    // One entry for each enforcement point. The routed-skill child is the second child.
    private static string[] Expected(TrustAudience audience)
    {
        var personal = audience == TrustAudience.Personal;
        return
        [
            $"parent run scope={audience}",
            $"set_webhook by name ran={personal}",
            $"spawned child has set_webhook={personal}",
            $"routed child has set_webhook={personal}",
            $"spawned child run scope={audience}",
            $"routed child run scope={audience}"
        ];
    }

    private string[] Observed()
    {
        // The first model call of each child has no tool results. It shows the tools that the child got.
        var childToolSets = _childModel.ReceivedMessages
            .Select((messages, index) => (messages, index))
            .Where(call => !call.messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any()))
            .Select(call => _childModel.ReceivedToolNames[call.index].Contains(SetWebhook))
            .ToArray();
        var childAudiences = _childProbe.Audiences.ToArray();
        Assert.Equal(2, childToolSets.Length);
        Assert.Equal(2, childAudiences.Length);
        return
        [
            $"parent run scope={Assert.Single(_parentProbe.Audiences)}",
            $"set_webhook by name ran={!_webhook.Audiences.IsEmpty}",
            $"spawned child has set_webhook={childToolSets[0]}",
            $"routed child has set_webhook={childToolSets[1]}",
            $"spawned child run scope={childAudiences[0]}",
            $"routed child run scope={childAudiences[1]}"
        ];
    }

    // Runs one turn. It returns after the child run of the turn completes and the turn completes.
    private async Task RunAsync(SessionId sessionId, string content, TrustAudience audience)
    {
        var ct = TestContext.Current.CancellationToken;
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

        bool childDone = false, turnDone = false;
        while (!childDone || !turnDone)
        {
            var output = await subscriber.ExpectMsgAsync<object>(TimeSpan.FromSeconds(15), cancellationToken: ct);
            childDone |= output is SubAgentOutput { Phase: SubAgentPhase.Completed };
            turnDone |= output is TurnCompleted;
        }
    }

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
