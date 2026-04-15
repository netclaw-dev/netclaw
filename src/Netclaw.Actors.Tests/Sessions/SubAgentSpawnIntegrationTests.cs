using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public class SubAgentSpawnIntegrationTests : TestKit
{
    private const string MainIdentityMarker = "You are a test assistant with subagent support.";
    private const string AgentsLayerMarker = "[agents] This marker should never appear in routed subagent calls.";

    private readonly RecordingRoleChatClientProvider _clientProvider = new();
    private RecordingContextTool? _recordingFileReadTool;

    public SubAgentSpawnIntegrationTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(_clientProvider);
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(MainIdentityMarker));
        services.AddSingleton<IReadOnlyList<IContextLayerProvider>>(
        [
            new StaticContextLayerProvider(AgentsLayerMarker, ContextLayerTiming.OnceAtStart)
        ]);

        var skillRoot = Path.Combine(Path.GetTempPath(), $"netclaw-skill-routing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(skillRoot);

        var routedSkillDir = Path.Combine(skillRoot, "ops-route");
        Directory.CreateDirectory(routedSkillDir);
        var routedSkillFile = Path.Combine(routedSkillDir, "SKILL.md");
        File.WriteAllText(routedSkillFile, """
            ---
            name: ops-route
            description: Route to operations helper.
            metadata:
              subagent: summarizer
            ---

            # Ops Route

            You specialize in daemon health checks.
            """);

        var missingSkillDir = Path.Combine(skillRoot, "missing-route");
        Directory.CreateDirectory(missingSkillDir);
        var missingSkillFile = Path.Combine(missingSkillDir, "SKILL.md");
        File.WriteAllText(missingSkillFile, """
            ---
            name: missing-route
            description: Route to a missing subagent.
            metadata:
              subagent: does-not-exist
            ---

            # Missing Route
            """);

        var restrictiveSkillDir = Path.Combine(skillRoot, "ops-route-restrictive");
        Directory.CreateDirectory(restrictiveSkillDir);
        var restrictiveSkillFile = Path.Combine(restrictiveSkillDir, "SKILL.md");
        File.WriteAllText(restrictiveSkillFile, """
            ---
            name: ops-route-restrictive
            description: Route to operations helper with restrictive allowed-tools metadata.
            allowed-tools: web_fetch
            metadata:
              subagent: summarizer
            ---

            # Ops Route Restrictive

            You specialize in daemon health checks.
            """);

        var skillRegistry = new SkillRegistry();
        skillRegistry.Register(new SkillEntry("ops-route", "Ops Route", "Route to operations helper.", routedSkillFile, routedSkillDir, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "summarizer"
        });
        skillRegistry.Register(new SkillEntry("missing-route", "Missing Route", "Route to missing subagent.", missingSkillFile, missingSkillDir, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "does-not-exist"
        });
        skillRegistry.Register(new SkillEntry("ops-route-restrictive", "Ops Route Restrictive", "Route to operations helper with restrictive metadata.", restrictiveSkillFile, restrictiveSkillDir, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "summarizer",
            AllowedTools = "web_fetch"
        });
        services.AddSingleton(skillRegistry);

        var registry = new ToolRegistry();
        var toolAccessPolicy = new ToolAccessPolicy(
            new ToolConfig(),
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());
        var subAgentRegistry = new SubAgentDefinitionRegistry();
        var subAgentPaths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-subagents-{Guid.NewGuid():N}"));
        subAgentPaths.EnsureDirectoriesExist();
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

        var spawner = new SubAgentSpawner(
            _clientProvider,
            registry,
            toolAccessPolicy,
            approvalService: null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SubAgentSpawner>.Instance);

        registry.Register(new SpawnAgentTool(subAgentRegistry, spawner, subAgentPaths));
        _recordingFileReadTool = new RecordingContextTool("file_read", "stub file content", "file");
        registry.Register(_recordingFileReadTool);

        services.AddSingleton(registry);
        services.AddSingleton(subAgentRegistry);
        services.AddSingleton(spawner);
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(
            registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatchingToolExecutor>.Instance));
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());

        services.AddTestNetclawPaths();
        services.AddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<NetclawPaths>()));
        services.AddSingleton(sp => new SessionToolServices(
            sp.GetRequiredService<IToolExecutor>(),
            sp.GetService<IToolAuditLogger>(),
            sp.GetRequiredService<ToolRegistry>(),
            sp.GetService<ToolAccessPolicy>(),
            sp.GetService<Netclaw.Actors.Channels.TrustContextDeriver>(),
            sp.GetService<Netclaw.Actors.Skills.SkillRegistry>(),
            sp.GetService<IToolApprovalService>(),
            sp.GetService<SubAgentDefinitionRegistry>(),
            sp.GetService<SubAgentSpawner>()));
        services.AddSingleton(sp => new SessionMemoryServices(
            sp.GetService<IMemoryExtractor>() ?? NullMemoryExtractor.Instance,
            sp.GetService<IMemoryRecallCoordinator>() ?? NullMemoryRecallCoordinator.Instance,
            sp.GetService<IMemoryCheckpointSink>() ?? NullMemoryCheckpointSink.Instance,
            sp.GetService<SQLiteMemoryStore>()));
        services.AddSingleton(new SessionObservability(null, null));
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    }

    [Fact]
    public async Task Spawn_agent_runs_under_session_and_emits_subagent_events()
    {
        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-spawn",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Summarize src/README.md"
                })
        ];

        var sessionId = new SessionId("test-channel/subagent-integration");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a subagent to summarize the file"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var toolCall = await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("spawn_agent", toolCall.ToolName);

        var started = await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentPhase.Started, started.Phase);
        Assert.Equal("summarizer", started.AgentName);
        Assert.Equal(1, started.ToolCount);

        var completed = await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.Equal("summarizer", completed.AgentName);
        Assert.True(completed.Success);
        Assert.Equal(0, completed.FindingsCount);
        Assert.Null(completed.MemoryDecision);

        // Drain the tool result output for spawn_agent emitted after tool execution
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _clientProvider.Main.CallCount);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);

        var subagentCall = Assert.Single(_clientProvider.Compaction.ReceivedMessages);
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System && string.Equals(m.Text, "You are a summarizer.", StringComparison.Ordinal));
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User && string.Equals(m.Text, "Summarize src/README.md", StringComparison.Ordinal));
        Assert.DoesNotContain(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System && (m.Text?.Contains("test assistant with subagent support", StringComparison.Ordinal) ?? false));
        Assert.DoesNotContain(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User && (m.Text?.Contains("Use a subagent to summarize the file", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task Routed_slash_command_with_unknown_subagent_fails_loud_without_inline_fallback()
    {
        var sessionId = new SessionId("test-channel/routed-slash-missing");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-missing-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/missing-route check health"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("not registered", text.Text, StringComparison.OrdinalIgnoreCase);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Skipped, completed.Outcome);

        Assert.Equal(0, _clientProvider.Main.CallCount);
        Assert.Equal(0, _clientProvider.Compaction.CallCount);
    }

    [Fact]
    public async Task Routed_slash_command_executes_with_overlay_and_isolated_prompt_stack()
    {
        var sessionId = new SessionId("test-channel/routed-slash-success");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-success-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check daemon health"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var text = await ExpectTextOutputAsync(subscriber, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        await ExpectTurnCompletedAsync(subscriber, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(0, _clientProvider.Main.CallCount);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);

        var subagentCall = Assert.Single(_clientProvider.Compaction.ReceivedMessages);
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains("You are a summarizer.", StringComparison.Ordinal) ?? false));
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains("[Skill Overlay]", StringComparison.Ordinal) ?? false));
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains("You specialize in daemon health checks.", StringComparison.Ordinal) ?? false));
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User
            && string.Equals(m.Text, "check daemon health", StringComparison.Ordinal));
        Assert.DoesNotContain(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains(MainIdentityMarker, StringComparison.Ordinal) ?? false));
        Assert.DoesNotContain(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains(AgentsLayerMarker, StringComparison.Ordinal) ?? false));
        Assert.DoesNotContain(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User
            && (m.Text?.Contains("Context:", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task Reminder_sourced_slash_command_routes_like_normal_slash_dispatch()
    {
        var sessionId = new SessionId("test-channel/routed-slash-reminder");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-reminder-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check scheduled health",
            Source = BuildReminderSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await ExpectTextOutputAsync(subscriber, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(subscriber, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(0, _clientProvider.Main.CallCount);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);

        var subagentCall = Assert.Single(_clientProvider.Compaction.ReceivedMessages);
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User
            && string.Equals(m.Text, "check scheduled health", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Routed_slash_ignores_skill_allowed_tools_for_runtime_authorization_and_inherits_audience()
    {
        _clientProvider.Compaction.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-read",
                "file_read",
                new Dictionary<string, object?>
                {
                    ["Path"] = "README.md"
                })
        ];

        var sessionId = new SessionId("test-channel/routed-slash-restrictive");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-restrictive-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var source = BuildReminderSource();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route-restrictive run health check",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var text = await ExpectTextOutputAsync(subscriber, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(subscriber, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _clientProvider.Main.CallCount);
        Assert.Equal(2, _clientProvider.Compaction.CallCount);
        Assert.NotNull(_recordingFileReadTool);
        Assert.True(_recordingFileReadTool!.WasCalled);
        Assert.Equal(TrustAudience.Team.ToWireValue(), _recordingFileReadTool.LastContext?.Audience);
        Assert.Equal(source.Boundary, _recordingFileReadTool.LastContext?.Boundary);
    }

    private static MessageSource BuildReminderSource()
    {
        return new MessageSource
        {
            ChannelType = ChannelType.Reminder,
            SenderId = "reminder-executor",
            Audience = TrustAudience.Team,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromChannelType(ChannelType.Reminder.ToWireValue(), TrustAudience.Team),
            ReceivedAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task<TextOutput> ExpectTextOutputAsync(Akka.TestKit.TestProbe probe, TimeSpan timeout, CancellationToken ct)
    {
        for (var i = 0; i < 8; i++)
        {
            var msg = await probe.ExpectMsgAsync<SessionOutput>(timeout, cancellationToken: ct);
            if (msg is TextOutput text)
                return text;
        }

        throw new Xunit.Sdk.XunitException("Expected TextOutput but only received non-text session outputs.");
    }

    private static async Task<TurnCompleted> ExpectTurnCompletedAsync(Akka.TestKit.TestProbe probe, TimeSpan timeout, CancellationToken ct)
    {
        for (var i = 0; i < 8; i++)
        {
            var msg = await probe.ExpectMsgAsync<SessionOutput>(timeout, cancellationToken: ct);
            if (msg is TurnCompleted completed)
                return completed;
        }

        throw new Xunit.Sdk.XunitException("Expected TurnCompleted but only received other session outputs.");
    }

    private sealed class RecordingRoleChatClientProvider : IChatClientProvider
    {
        public FakeChatClient Main { get; } = new();
        public FakeChatClient Compaction { get; } = new();

        public IChatClient GetClient(ModelRole role)
            => role == ModelRole.Compaction ? Compaction : Main;
    }

    private sealed class RecordingContextTool(string name, string result, string grantCategory = "builtin") : INetclawTool
    {
        public string Name { get; } = name;
        public string Description => "Recording fake tool";
        public string GrantCategory { get; } = grantCategory;
        public System.Text.Json.JsonElement ParameterSchema => default;

        public bool WasCalled { get; private set; }
        public ToolExecutionContext? LastContext { get; private set; }

        public AITool ToAITool() => AIFunctionFactory.Create(() => result, name: Name, description: Description);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => ExecuteAsync(arguments, ToolExecutionContext.Empty, ct);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolExecutionContext context, CancellationToken ct = default)
        {
            WasCalled = true;
            LastContext = context;
            return Task.FromResult(result);
        }
    }

    private sealed class StaticContextLayerProvider(string content, ContextLayerTiming timing) : IContextLayerProvider
    {
        public ContextLayerTiming Timing => timing;

        public string GetContextLayer() => content;
    }
}
