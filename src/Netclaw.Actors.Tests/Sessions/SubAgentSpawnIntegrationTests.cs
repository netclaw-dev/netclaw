using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public class SubAgentSpawnIntegrationTests : TestKit
{
    private readonly RecordingRoleChatClientProvider _clientProvider = new();

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
                MemorySidecarsEnabled = false,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with subagent support."));

        var registry = new ToolRegistry();
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

        registry.Register(new SpawnAgentTool(
            subAgentRegistry,
            new SubAgentSpawner(
                _clientProvider,
                registry,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SubAgentSpawner>.Instance)));
        registry.Register(new FakeNetclawTool("file_read", "stub file content", "file"));

        services.AddSingleton(registry);
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(
            registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatchingToolExecutor>.Instance));
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());

        // Composite records for LlmSessionActor constructor
        services.AddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<NetclawPaths>()));
        services.AddSingleton(sp => new SessionToolServices(
            sp.GetRequiredService<IToolExecutor>(),
            sp.GetService<IToolAuditLogger>(),
            sp.GetRequiredService<ToolRegistry>(),
            sp.GetService<ToolAccessPolicy>(),
            sp.GetService<Netclaw.Actors.Channels.TrustContextDeriver>(),
            sp.GetService<Netclaw.Actors.Skills.SkillRegistry>()));
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
        }, TimeSpan.FromSeconds(10));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a subagent to summarize the file"
        }, TimeSpan.FromSeconds(3));

        var toolCall = await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal("spawn_agent", toolCall.ToolName);

        var started = await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(SubAgentPhase.Started, started.Phase);
        Assert.Equal("summarizer", started.AgentName);
        Assert.Equal(1, started.ToolCount);

        var completed = await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.Equal("summarizer", completed.AgentName);
        Assert.True(completed.Success);
        Assert.Equal(0, completed.FindingsCount);
        Assert.Null(completed.MemoryDecision);

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5));
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

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

    private sealed class RecordingRoleChatClientProvider : IChatClientProvider
    {
        public FakeChatClient Main { get; } = new();
        public FakeChatClient Compaction { get; } = new();

        public IChatClient GetClient(ModelRole role)
            => role == ModelRole.Compaction ? Compaction : Main;
    }
}
