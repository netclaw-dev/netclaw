using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Streams;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration test that exercises the full Netclaw actor pipeline:
/// message routing → session actor → IChatClient → strongly-typed output delivery.
/// Subscribers join sessions directly via <see cref="JoinSession"/> and receive
/// <see cref="SessionOutput"/> events filtered by <see cref="OutputFilter"/>.
/// </summary>
public class LlmSessionIntegrationTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-03-21T12:00:00Z"));
    private readonly RecordingSessionLifecycleObserver _lifecycleObserver = new();

    public LlmSessionIntegrationTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.FromMinutes(1),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
                MemorySidecarsEnabled = false,
                DiscoveredToolRetentionTurns = 3,
                DiscoveredToolMaxCount = 12,
            }
        });
        services.AddSingleton(new ReminderConfig());
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<SidecarRecallPlanner>();
        services.AddSingleton<MemoryProposalGate>();
        services.AddSingleton<RecallPlanGate>();
        services.AddSingleton<IMemoryCheckpointSink, NullMemoryCheckpointSink>();
        services.AddSingleton<SQLiteMemoryStore>(sp => new SQLiteMemoryStore(Path.Combine(Path.GetTempPath(), $"netclaw-sidecar-tests-{Guid.NewGuid():N}.db"), TimeProvider.System));
        services.AddSingleton<IMemoryRecallCoordinator>(sp => new SQLiteMemoryRecallCoordinator(
            sp.GetRequiredService<SQLiteMemoryStore>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<SidecarRecallPlanner>(),
            sp.GetRequiredService<RecallPlanGate>(),
            sessionConfig: sp.GetRequiredService<SessionConfig>()));

        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create((string url) => "ok", "navigate_page", "Navigate to URL"),
            "browser_chrome_devtools",
            "navigate_page"));
        registry.Register(new SearchToolsTool(registry));

        services.AddSingleton(registry);
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddSingleton<ISessionLifecycleObserver>(_lifecycleObserver);
        services.AddSingleton<ISessionPipeline>(new UnusedSessionPipeline());

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
        services.AddSingleton(sp => new SessionObservability(
            sp.GetService<Telemetry.ISessionMetrics>(),
            sp.GetService<ISessionLifecycleObserver>()));
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawActors();
    }

    [Fact]
    public async Task JoinSession_receives_SessionJoined_acknowledgement()
    {
        var sessionId = new SessionId("test-channel/join-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("join-probe");

        var joined = await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, joined.SessionId);
        Assert.Equal(0, joined.TurnCount);
        Assert.Null(joined.Title);
    }

    [Fact]
    public async Task SendUserMessage_delivers_TextOutput_and_TurnCompleted()
    {
        var sessionId = new SessionId("test-channel/test-thread");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("adapter-probe");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

        var ack = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, Netclaw!"
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, ack.SessionId);

        // Subscriber receives typed output events
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, text.SessionId);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, completed.SessionId);
        Assert.Equal(1, completed.TurnNumber);
    }

    [Fact]
    public async Task Repeated_pre_tool_empty_responses_fail_turn_and_allow_followup_prompt()
    {
        _fakeChatClient.PlannedResponses.Enqueue([]);
        _fakeChatClient.PlannedResponses.Enqueue([]);
        _fakeChatClient.PlannedResponses.Enqueue([]);

        var sessionId = new SessionId("test-channel/pre-tool-empty");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("pre-tool-empty-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Please answer"
        }, TimeSpan.FromSeconds(3));

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.Equal(sessionId, error.SessionId);
        Assert.Equal(ErrorCategory.ProviderFailure, error.Category);
        Assert.Contains("Please try rephrasing", error.Message, StringComparison.OrdinalIgnoreCase);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, completed.SessionId);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Try again"
        }, TimeSpan.FromSeconds(3));

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Contains("[fake] Response #4", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Empty_response_after_tool_nudge_fails_turn_and_allows_followup_prompt()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        ];
        _fakeChatClient.PlannedResponses.Enqueue([]);
        _fakeChatClient.PlannedResponses.Enqueue([]);

        var sessionId = new SessionId("test-channel/post-tool-empty");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("post-tool-empty-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));
        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6));
        Assert.Equal(sessionId, error.SessionId);
        Assert.Equal(ErrorCategory.ProviderFailure, error.Category);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Try again after the failure"
        }, TimeSpan.FromSeconds(3));

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Contains("[fake] Response #4", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Delivery_failed_for_latest_turn_retries_once_with_structured_nudge()
    {
        var sessionId = new SessionId("test-channel/delivery-retry");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("delivery-retry-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Say hello"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = completed.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.MessageTooLarge,
            ErrorMessage = "msg_too_long"
        });

        var retried = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Contains("Response #2", retried.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.Contains(_fakeChatClient.ReceivedMessages[^1], msg =>
            msg.Role == Microsoft.Extensions.AI.ChatRole.User
            && msg.Text is not null
            && msg.Text.Contains("msg_too_long", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stale_delivery_failed_is_ignored_after_new_user_turn_starts()
    {
        var sessionId = new SessionId("test-channel/stale-delivery-failure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("stale-delivery-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "first"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        var firstCompleted = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = firstCompleted.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.ContentRejected,
            ErrorMessage = "invalid_blocks"
        });

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Delivery_failed_while_processing_newer_turn_is_ignored()
    {
        var sessionId = new SessionId("test-channel/processing-delivery-failure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("processing-delivery-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "first"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        var firstCompleted = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeChatClient.NextResponseGate = gate;

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3));

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = firstCompleted.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.ContentRejected,
            ErrorMessage = "invalid_blocks"
        });

        gate.TrySetResult();

        var secondText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Contains("Response #2", secondText.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Delivery_retry_budget_stops_after_two_failed_corrections()
    {
        var sessionId = new SessionId("test-channel/delivery-retry-budget");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("delivery-budget-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "first"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            sessionManager.Tell(new DeliveryFailed
            {
                SessionId = sessionId,
                TurnNumber = completed.TurnNumber,
                ChannelType = ChannelType.Slack,
                FailureKind = DeliveryFailureKind.ContentRejected,
                ErrorMessage = "invalid_blocks"
            });

            await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
            completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        }

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = completed.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.ContentRejected,
            ErrorMessage = "invalid_blocks"
        });

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Transport_failure_injects_nudge_without_triggering_retry()
    {
        var sessionId = new SessionId("test-channel/transport-failure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("transport-failure-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Say hello"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Send non-retryable transport failure — should NOT trigger LLM retry
        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = completed.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.TransportFailure,
            ErrorMessage = "Timed out posting reply"
        });

        // No retry should occur — transport failures can't be fixed by changing output
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));

        // On the next user message, the LLM should see the transport failure nudge
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Are you there?"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.Contains(_fakeChatClient.ReceivedMessages[^1], msg =>
            msg.Role == Microsoft.Extensions.AI.ChatRole.User
            && msg.Text is not null
            && msg.Text.Contains("transport error", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unknown_delivery_failure_injects_nudge_without_triggering_retry()
    {
        var sessionId = new SessionId("test-channel/unknown-failure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("unknown-failure-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Say hello"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Send non-retryable unknown failure — should NOT trigger LLM retry
        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = completed.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.Unknown,
            ErrorMessage = "Unexpected Slack error"
        });

        // No retry should occur
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));

        // On the next user message, the nudge should be visible in LLM context
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Are you there?"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.Contains(_fakeChatClient.ReceivedMessages[^1], msg =>
            msg.Role == Microsoft.Extensions.AI.ChatRole.User
            && msg.Text is not null
            && msg.Text.Contains("unknown delivery error", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sidecar_observation_promotes_strong_user_assertion_into_memory()
    {
        var gate = new MemoryProposalGate();
        var observer = new SidecarMemoryObserver();
        var request = observer.BuildRequest(
            "slack/test-memory",
            "turn-1",
            "turn_completed",
            "project:slack",
            "normal",
            "I always fly out of IAH and I use United Airlines.",
            "Understood.",
            ["I always fly out of IAH and I use United Airlines."],
            [],
            ["I always fly out of IAH and I use United Airlines."],
            ["Understood."],
            [],
            false,
            DateTimeOffset.UtcNow);

        var response = await _fakeChatClient.GetResponseAsync(new[]
        {
            new ChatMessage(Microsoft.Extensions.AI.ChatRole.System, MemorySidecarPromptBuilder.BuildMemoryObservationSystemPrompt()),
            new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, MemorySidecarPromptBuilder.BuildMemoryObservationUserPrompt(request))
        });

        var proposals = JsonSerializer.Deserialize<IReadOnlyList<MemoryProposal>>(
            response.Messages[^1].Text!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var accepted = gate.Accept(proposals!, "project:slack", "normal", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Contains(accepted, x => x.Title.Contains("Preferred Airline", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(accepted, x => x.Title.Contains("Origin Airport", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OutputFilter_controls_which_content_categories_are_delivered()
    {
        _fakeChatClient.IncludeThinking = true;
        var sessionId = new SessionId("test-channel/filter-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var textOnlySub = CreateTestProbe("text-only");
        var textAndUsageSub = CreateTestProbe("text-usage");
        var fullSub = CreateTestProbe("full");

        // Three subscribers with different filter bitmasks — sequential Ask
        // ensures each join is fully processed before the next
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = textOnlySub,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await textOnlySub.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = textAndUsageSub,
            Filter = OutputFilter.TextAndUsage
        }, TimeSpan.FromSeconds(3));
        await textAndUsageSub.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = fullSub,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        await fullSub.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Think about this"
        }, TimeSpan.FromSeconds(3));

        // TextOnly: TextOutput + TurnCompleted (lifecycle always delivered)
        await textOnlySub.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await textOnlySub.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        await textOnlySub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200));

        // TextAndUsage: TextOutput + UsageOutput + TurnCompleted (no thinking)
        await textAndUsageSub.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await textAndUsageSub.ExpectMsgAsync<UsageOutput>(TimeSpan.FromSeconds(3));
        await textAndUsageSub.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        await textAndUsageSub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200));

        // Full: ThinkingOutput + TextOutput + UsageOutput + TurnCompleted
        await fullSub.ExpectMsgAsync<ThinkingOutput>(TimeSpan.FromSeconds(3));
        await fullSub.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        var usage = await fullSub.ExpectMsgAsync<UsageOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(128_000, usage.ContextWindowTokens);
        Assert.NotNull(usage.UsagePercent);
        Assert.True(usage.UsagePercent > 0);
        await fullSub.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        await fullSub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Two_sessions_are_routed_independently()
    {
        var session1 = new SessionId("channel-A/thread-1");
        var session2 = new SessionId("channel-B/thread-2");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var sub1 = CreateTestProbe("adapter-1");
        var sub2 = CreateTestProbe("adapter-2");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = session1,
            Subscriber = sub1,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await sub1.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = session2,
            Subscriber = sub2,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await sub2.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session1,
            Content = "Message for session 1",
        }, TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session2,
            Content = "Message for session 2"
        }, TimeSpan.FromSeconds(3));

        // Each subscriber only gets its own session's output
        var text1 = await sub1.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(session1, text1.SessionId);
        await sub1.ExpectMsgAsync<TurnCompleted>();

        var text2 = await sub2.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal(session2, text2.SessionId);
        await sub2.ExpectMsgAsync<TurnCompleted>();

        await sub1.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200));
        await sub2.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Buffered_messages_are_batched_into_follow_up_LLM_call()
    {
        _fakeChatClient.Delay = TimeSpan.FromMilliseconds(200);
        var sessionId = new SessionId("channel-C/thread-3");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("adapter-batch");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

        // First message — actor enters Processing
        var ack1 = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, ack1.SessionId);

        // These two are deterministically buffered
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message"
        }, TimeSpan.FromSeconds(3));
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Third message"
        }, TimeSpan.FromSeconds(3));

        // First turn output
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6));

        // Second turn output (batched follow-up)
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6));

        // Only two LLM calls total
        Assert.Equal(2, _fakeChatClient.CallCount);

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Discovered_tools_are_retained_then_expire_after_lease_window()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-search", "search_tools",
                new Dictionary<string, object?> { ["Query"] = "browser_chrome_devtools" })
        ];

        _fakeToolExecutor.Results["search_tools"] =
            "Found 1 tool(s):\n\n"
            + "  browser_chrome_devtools/navigate_page — Navigate to URL (params: url)\n\n"
            + "Call any tool above by its full name. Tools are now loaded and available.";

        var sessionId = new SessionId("channel-discovery/thread-retention");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("discovery-retention-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Find browser tools"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6));

        for (var i = 0; i < 4; i++)
        {
            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = $"Follow-up turn {i + 1}"
            }, TimeSpan.FromSeconds(3));

            await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6));
            await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6));
        }

        Assert.True(_fakeChatClient.ReceivedToolNames.Count >= 6);

        Assert.Contains("browser_chrome_devtools/navigate_page", _fakeChatClient.ReceivedToolNames[2]);
        Assert.Contains("browser_chrome_devtools/navigate_page", _fakeChatClient.ReceivedToolNames[3]);
        Assert.Contains("browser_chrome_devtools/navigate_page", _fakeChatClient.ReceivedToolNames[4]);
        Assert.DoesNotContain("browser_chrome_devtools/navigate_page", _fakeChatClient.ReceivedToolNames[5]);
    }

    [Fact]
    public async Task Preamble_text_surfaces_before_tool_calls()
    {
        // Configure: LLM returns preamble text alongside tool calls
        _fakeChatClient.PreambleText = "Let me search for that...";
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-p1", "browser_chrome_devtools/navigate_page",
                new Dictionary<string, object?> { ["url"] = "https://example.com" })
        ];
        _fakeToolExecutor.Results["browser_chrome_devtools/navigate_page"] = "page loaded";

        var sessionId = new SessionId("test-channel/preamble-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("preamble-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3));

        // Preamble text should arrive before tool calls
        var preamble = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5));
        Assert.Equal("Let me search for that...", preamble.Text);

        // BufferFlush should arrive after preamble text
        await subscriber.ExpectMsgAsync<BufferFlush>(TimeSpan.FromSeconds(3));

        // Then tool call
        var toolCall = await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal("browser_chrome_devtools/navigate_page", toolCall.ToolName);

        // After tool execution and follow-up LLM call, final text response
        var finalText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5));
        Assert.Contains("fake", finalText.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Non_contiguous_TextContent_items_produce_single_TextOutput()
    {
        // Simulate a response where ToChatResponse() produces non-contiguous
        // TextContent items: [text, tool_call, text]. This happens when the
        // provider returns text both before and after tool calls in the same
        // response message. Without consolidation, each TextContent would be
        // emitted as a separate TextOutput → duplicate Slack posts.
        _fakeChatClient.PlannedResponses.Enqueue(new AIContent[]
        {
            new TextContent("Part one"),
            new FunctionCallContent("call-nc1", "browser_chrome_devtools/navigate_page",
                new Dictionary<string, object?> { ["url"] = "https://example.com" }),
            new TextContent("Part two")
        });
        _fakeToolExecutor.Results["browser_chrome_devtools/navigate_page"] = "page loaded";

        var sessionId = new SessionId("test-channel/non-contiguous-text");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("nc-sub");

        // Subscribe to Text + ToolCalls only (not TextStreaming) to match
        // Slack adapter behavior and avoid streaming delta noise.
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Text | OutputFilter.ToolCalls
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Do something with tools"
        }, TimeSpan.FromSeconds(3));

        // Should receive exactly ONE consolidated TextOutput for the preamble
        var preamble = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5));
        Assert.Contains("Part one", preamble.Text);
        Assert.Contains("Part two", preamble.Text);

        // Tool call output
        var toolCall = await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3));
        Assert.Equal("browser_chrome_devtools/navigate_page", toolCall.ToolName);

        // Final text response after tool execution
        var finalText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5));
        Assert.Contains("fake", finalText.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Session_recovers_state_after_actor_is_killed()
    {
        var sessionId = new SessionId("test-channel/recovery-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("recovery-sub");

        // Phase 1: Build up state — two completed turns
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message"
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Phase 2: Kill the session actor child
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child);

        // Phase 3: Recover — send JoinSession to the same session ID.
        // GenericChildPerEntityParent creates a new actor that recovers from journal.
        var recoverSub = CreateTestProbe("recovery-sub-2");
        var recovered = await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = recoverSub,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(5));
        await recoverSub.ExpectMsgAsync<SessionJoined>(); // Drain subscriber notification
        Assert.Equal(sessionId, recovered.SessionId);
        Assert.Equal(2, recovered.TurnCount); // Both turns recovered

        // Phase 4: Verify the session still works — send a third message
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Third message after recovery"
        }, TimeSpan.FromSeconds(3));
        var text = await recoverSub.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        var completed = await recoverSub.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        Assert.Equal(3, completed.TurnNumber); // Continues from recovered state
    }

    [Fact]
    public async Task Rejoin_suppresses_duplicate_SessionJoined_on_subscriber()
    {
        var sessionId = new SessionId("test-channel/rejoin-suppress");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("rejoin-sub");

        // First join — subscriber receives SessionJoined
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        // Re-join via Tell (piggybacked path) — subscriber should NOT get a duplicate
        sessionManager.Tell(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, ActorRefs.NoSender);

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));

        // Re-join via Ask still returns SessionJoined to the caller (not the subscriber)
        var rejoined = await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, rejoined.SessionId);

        // Subscriber still should not have received a duplicate
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Session_does_not_passivate_with_active_subscribers()
    {
        var sessionId = new SessionId("test-channel/no-passivate-sub");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("active-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        // Resolve session actor
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);

        // Send ReceiveTimeout directly — with active subscriber, should be deferred
        child.Tell(ReceiveTimeout.Instance);

        // Actor should still be alive
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));

        // Session should still process messages
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Still alive?"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Session_idle_timeout_deactivates_only_when_actor_passivates()
    {
        var sessionId = new SessionId("test-channel/deactivate-on-passivate");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("deactivate-passivate-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);

        child.Tell(ReceiveTimeout.Instance);
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);

        child.Tell(new LeaveSession
        {
            SessionId = sessionId,
            Subscriber = subscriber
        });

        child.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5));

        Assert.Contains(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }

    [Fact]
    public async Task Ready_session_drains_for_daemon_restart()
    {
        var sessionId = new SessionId("test-channel/restart-drain-ready");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("restart-drain-ready-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}").ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);

        var ack = await sessionManager.Ask<CommandAck>(new PrepareForDaemonRestart
        {
            SessionId = sessionId,
            Reason = "config-reload"
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(sessionId, ack.SessionId);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5));
        Assert.Contains(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }

    [Fact]
    public async Task Processing_session_rejects_new_work_and_passivates_after_current_turn()
    {
        var sessionId = new SessionId("test-channel/restart-drain-processing");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("restart-drain-processing-sub");
        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeChatClient.NextResponseGate = responseGate;

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3));

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}").ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);

        var drainTask = sessionManager.Ask<CommandAck>(new PrepareForDaemonRestart
        {
            SessionId = sessionId,
            Reason = "config-reload"
        }, TimeSpan.FromSeconds(5));

        var nack = await sessionManager.Ask<CommandNack>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Should be rejected"
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(SessionIngressGate.RestartInProgressMessage, nack.Reason);

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = 999,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.MessageTooLarge,
            ErrorMessage = "msg_too_long"
        });

        responseGate.TrySetResult();

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, (await drainTask).SessionId);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(_fakeChatClient.ReceivedMessages, conversation =>
            conversation.Any(msg =>
                msg.Text is not null
                && msg.Text.Contains("msg_too_long", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Compacting_session_rejects_new_work_and_passivates_after_compaction_finishes()
    {
        var sessionId = new SessionId("test-channel/restart-drain-compacting");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("restart-drain-compacting-sub");
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 200_000,
            OutputTokenCount = 1,
            TotalTokenCount = 200_001
        };
        _fakeChatClient.HangingObservationCallsRemaining = 1;

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Trigger compaction"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}").ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);

        var drainTask = sessionManager.Ask<CommandAck>(new PrepareForDaemonRestart
        {
            SessionId = sessionId,
            Reason = "config-reload"
        }, TimeSpan.FromSeconds(5));

        var nack = await sessionManager.Ask<CommandNack>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Should be rejected during compaction"
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(SessionIngressGate.RestartInProgressMessage, nack.Reason);

        child.Tell(new CompactionFailed
        {
            Cause = new InvalidOperationException("test compaction completion")
        });

        Assert.Equal(sessionId, (await drainTask).SessionId);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5));
        Assert.Contains(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }

    [Fact]
    public async Task Warm_session_injects_restart_notice_on_next_turn()
    {
        var sessionId = new SessionId("test-channel/restart-warm-notice");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("restart-warm-notice-sub");

        await sessionManager.Ask<CommandAck>(new WarmSession
        {
            SessionId = sessionId,
            RestartNotice = "The daemon restarted due to a configuration change. Recovery resumed from the last durable checkpoint."
        }, TimeSpan.FromSeconds(3));

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello after restart"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        Assert.Contains(_fakeChatClient.ReceivedMessages, conversation =>
            conversation.Any(msg =>
                msg.Role == Microsoft.Extensions.AI.ChatRole.System
                && msg.Text is not null
                && msg.Text.Contains("Recovery resumed from the last durable checkpoint", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Subscriber_survives_session_actor_passivation_via_piggyback_rejoin()
    {
        var sessionId = new SessionId("test-channel/passivation-rejoin");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriberA = CreateTestProbe("sub-a");

        // Phase 1: Join with subscriber A, complete a turn
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriberA,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriberA.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3));
        await subscriberA.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriberA.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Phase 2: Kill session actor (simulating passivation)
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child);

        // Phase 3: Join with subscriber B (triggers re-creation)
        var subscriberB = CreateTestProbe("sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriberB,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(5));
        await subscriberB.ExpectMsgAsync<SessionJoined>();

        // Phase 4: Piggybacked JoinSession for A + SendUserMessage
        // This simulates what ChannelPipeline does on each inbound message
        sessionManager.Tell(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriberA,
            Filter = OutputFilter.TextOnly
        }, ActorRefs.NoSender);

        await subscriberA.ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "After passivation"
        }, TimeSpan.FromSeconds(3));

        // Both subscribers should receive output
        await subscriberA.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriberA.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        await subscriberB.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        await subscriberB.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task New_user_message_during_passivation_aborts_shutdown_and_processes_message()
    {
        var sessionId = new SessionId("test-channel/passivation-new-message");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("passivation-new-message-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);

        child.Tell(new LeaveSession
        {
            SessionId = sessionId,
            Subscriber = subscriber
        });

        child.Tell(ReceiveTimeout.Instance);

        var ack = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Interrupt passivation"
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(sessionId, ack.SessionId);
        await AwaitAssertAsync(() =>
        {
            Assert.Equal(1, _fakeChatClient.CallCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));

        var rejoined = await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(1, rejoined.TurnCount);
        await subscriber.ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3));
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }

    [Fact]
    public async Task Delivery_feedback_during_passivation_aborts_shutdown_and_retries_latest_turn()
    {
        var sessionId = new SessionId("test-channel/passivation-delivery-feedback");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("passivation-delivery-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Original message"
        }, TimeSpan.FromSeconds(3));

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3));
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3));

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);

        child.Tell(new LeaveSession
        {
            SessionId = sessionId,
            Subscriber = subscriber
        });

        child.Tell(ReceiveTimeout.Instance);
        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = completed.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.MessageTooLarge,
            ErrorMessage = "msg_too_long"
        });

        await AwaitAssertAsync(() =>
        {
            Assert.True(_fakeChatClient.CallCount >= 2);
            Assert.Contains(_fakeChatClient.ReceivedMessages, conversation =>
                conversation.Any(msg =>
                    msg.Role == Microsoft.Extensions.AI.ChatRole.User
                    && msg.Text is not null
                    && msg.Text.Contains("msg_too_long", StringComparison.OrdinalIgnoreCase)));
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));

        var rejoined = await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(2, rejoined.TurnCount);
        await subscriber.ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3));
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }
}

/// <summary>
/// Fake IChatClient that returns canned responses for testing.
/// Supports configurable thinking tokens, usage data, and tool calls.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => _callCount;

    public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = [];
    public List<IReadOnlyList<string>> ReceivedToolNames { get; } = [];

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// When true, responses include TextReasoningContent and UsageDetails.
    /// </summary>
    public bool IncludeThinking { get; set; }

    /// <summary>
    /// When set, the first response returns these tool calls instead of text.
    /// Subsequent calls return normal text (simulating the LLM completing after tool results).
    /// When <see cref="AlwaysReturnToolCalls"/> is true, every call returns tool calls
    /// as long as tools are available in options (for testing iteration limits).
    /// </summary>
    public List<FunctionCallContent>? ToolCallsOnFirstCall { get; set; }

    /// <summary>
    /// When true, every call returns tool calls (from <see cref="ToolCallsOnFirstCall"/>)
    /// as long as <c>options.Tools</c> is non-empty. When tools are omitted from options
    /// (circuit breaker fired), returns normal text instead.
    /// </summary>
    public bool AlwaysReturnToolCalls { get; set; }

    /// <summary>
    /// When true, tool calls continue even if the caller omits tools from ChatOptions.
    /// Simulates providers that hallucinate tool calls after the circuit breaker fires.
    /// </summary>
    public bool IgnoreToolAvailability { get; set; }

    /// <summary>
    /// When set, tool-call responses include this text as a preamble alongside the
    /// <see cref="FunctionCallContent"/> items. Simulates the model producing
    /// user-facing text (e.g., "Let me search for that...") before executing tools.
    /// </summary>
    public string? PreambleText { get; set; }

    /// <summary>
    /// When set, all responses include this usage data.
    /// Used to simulate token counts that trigger compaction.
    /// </summary>
    public UsageDetails? UsageOverride { get; set; }

    /// <summary>
    /// Number of compaction observation sidecar calls that should hang until cancellation.
    /// Used to simulate providers that never return during compaction.
    /// </summary>
    public int HangingObservationCallsRemaining { get; set; }

    /// <summary>
    /// Number of compaction observation sidecar calls that should ignore cancellation
    /// entirely and never complete. Used to simulate a wedged compaction provider.
    /// </summary>
    public int StuckObservationCallsRemaining { get; set; }

    /// <summary>
    /// When populated, responses are dequeued in order before falling back to the
    /// default fake text response. Each entry is used as the assistant message contents.
    /// </summary>
    public Queue<IReadOnlyList<AIContent>> PlannedResponses { get; } = new();

    public TaskCompletionSource? NextResponseGate { get; set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        ReceivedMessages.Add(messageList);
        ReceivedToolNames.Add(options?.Tools?
            .Select(t => t is AIFunction f ? f.Name : t.GetType().Name)
            .ToList()
            ?? []);
        Interlocked.Increment(ref _callCount);

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        if (NextResponseGate is { } nextResponseGate)
        {
            NextResponseGate = null;
            using var registration = cancellationToken.Register(() => nextResponseGate.TrySetCanceled(cancellationToken));
            await nextResponseGate.Task;
        }

        var systemText = messageList.FirstOrDefault(m => m.Role == Microsoft.Extensions.AI.ChatRole.System)?.Text ?? string.Empty;
        var userText = messageList.LastOrDefault(m => m.Role == Microsoft.Extensions.AI.ChatRole.User)?.Text ?? string.Empty;

        if (systemText.Contains("You are a recall planning sidecar", StringComparison.Ordinal))
        {
            var request = JsonSerializer.Deserialize<RecallPlanningRequest>(userText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var terms = new List<string>();
            if (!string.IsNullOrWhiteSpace(request?.UserText))
                terms.AddRange(request.UserText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (request?.RecentEntities is not null)
                terms.AddRange(request.RecentEntities);

            var filtered = terms
                .Select(x => x.Trim(',', '.', '?', '!').ToLowerInvariant())
                .Where(x => x.Length >= 3)
                .Where(x => x is not ("what" or "should" or "there" or "some" or "give" or "with" or "from"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(request?.MaxQueryTerms ?? 8)
                .ToArray();

            var plan = new RecallQueryPlan(
                request?.Mode ?? "automatic",
                "test",
                request?.RecentEntities ?? [],
                [],
                filtered,
                request?.Mode == "intentional" ? ["durable_fact", "evidence"] : ["durable_fact"],
                Math.Min(request?.MaxResults ?? 3, 3),
                false);

            return new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                JsonSerializer.Serialize(plan)));
        }

        if (systemText.Contains("You are a memory observation sidecar", StringComparison.Ordinal))
        {
            var request = JsonSerializer.Deserialize<MemoryObservationRequest>(userText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var proposals = new List<MemoryProposal>();
            var assertions = request?.CurrentTurn.StrongAssertions ?? [];
            foreach (var assertion in assertions)
            {
                if (assertion.Contains("IAH", StringComparison.OrdinalIgnoreCase))
                {
                    proposals.Add(new MemoryProposal(
                        "upsert_document",
                        "durable_fact",
                        "user",
                        "self",
                        new MemoryAnchor("user-travel-origin", "preference"),
                        "Travel Profile: Primary Origin Airport",
                        "Primary origin airport: IAH",
                        ["origin airport", "fly out of", "IAH"],
                        ["travel_profile", "user_preference"],
                        ["origin_airport"],
                        null,
                        "auto",
                        "normal",
                        0.95,
                        null,
                        null,
                        null,
                        "strong user assertion"));
                }

                if (assertion.Contains("United", StringComparison.OrdinalIgnoreCase))
                {
                    proposals.Add(new MemoryProposal(
                        "upsert_document",
                        "durable_fact",
                        "user",
                        "self",
                        new MemoryAnchor("user-travel-airline", "preference"),
                        "Travel Profile: Preferred Airline",
                        "Preferred airline: United Airlines",
                        ["preferred airline", "united airlines", "usually fly"],
                        ["travel_profile", "user_preference"],
                        ["preferred_airline"],
                        null,
                        "auto",
                        "normal",
                        0.95,
                        null,
                        null,
                        null,
                        "strong user assertion"));
                }
            }

            return new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                JsonSerializer.Serialize<IReadOnlyList<MemoryProposal>>(proposals)));
        }

        if (systemText.Contains("You are an observation compressor", StringComparison.Ordinal))
        {
            if (StuckObservationCallsRemaining > 0)
            {
                StuckObservationCallsRemaining--;
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await gate.Task;
            }

            if (HangingObservationCallsRemaining > 0)
            {
                HangingObservationCallsRemaining--;
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
                await gate.Task;
            }

            return new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                "- compacted observation"));
        }

        // Return tool calls if configured
        if (ToolCallsOnFirstCall is not null)
        {
            var returnToolCalls = AlwaysReturnToolCalls
                ? (IgnoreToolAvailability || options?.Tools?.Count > 0)   // Every call, even when tools are withheld
                : _callCount == 1;            // First call only (existing behavior)

            if (returnToolCalls)
            {
                var toolCallContents = new List<AIContent>();
                if (!string.IsNullOrWhiteSpace(PreambleText))
                    toolCallContents.Add(new TextContent(PreambleText));
                toolCallContents.AddRange(ToolCallsOnFirstCall);
                var toolCallMessage = new ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    toolCallContents);
                var toolResponse = new ChatResponse(toolCallMessage);
                if (UsageOverride is not null)
                    toolResponse.Usage = UsageOverride;
                return toolResponse;
            }
        }

        if (PlannedResponses.Count > 0)
        {
            var plannedContents = PlannedResponses.Dequeue();
            var plannedResponse = new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                new List<AIContent>(plannedContents)));
            if (UsageOverride is not null)
                plannedResponse.Usage = UsageOverride;
            return plannedResponse;
        }

        var contents = new List<AIContent>();

        if (IncludeThinking)
        {
            contents.Add(new TextReasoningContent("[fake thinking] Let me consider..."));
        }

        contents.Add(new TextContent($"[fake] Response #{_callCount}"));

        var responseMessage = new ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant,
            contents);

        var response = new ChatResponse(responseMessage);

        if (UsageOverride is not null)
        {
            response.Usage = UsageOverride;
        }
        else if (IncludeThinking)
        {
            response.Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 20,
                TotalTokenCount = 30,
                ReasoningTokenCount = 5
            };
        }

        return response;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return CreateStreamingUpdatesAsync(messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> CreateStreamingUpdatesAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

internal sealed class FakeRecallCoordinator : IMemoryRecallCoordinator
{
    public AutomaticRecallResult Result { get; set; } = new([]);

    public Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
        => Task.FromResult(Result);
}

internal sealed class RecordingSessionLifecycleObserver : ISessionLifecycleObserver
{
    public List<string> ActivatedSessionIds { get; } = [];
    public List<string> DeactivatedSessionIds { get; } = [];

    public void OnSessionActivated(SessionId sessionId, ChannelType channelType)
        => ActivatedSessionIds.Add(sessionId.Value);

    public void OnOutput(SessionOutput output)
    {
    }

    public void OnSessionDeactivated(SessionId sessionId)
        => DeactivatedSessionIds.Add(sessionId.Value);
}

internal sealed class UnusedSessionPipeline : ISessionPipeline
{
    public Task<MaterializedSession> CreateAsync(
        SessionId sessionId,
        SessionPipelineOptions options,
        IMaterializer? materializer = null,
        CancellationToken cancellationToken = default)
        => Task.FromException<MaterializedSession>(new NotSupportedException("Session pipeline is not used by these tests."));

    public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
        => Task.CompletedTask;
}
