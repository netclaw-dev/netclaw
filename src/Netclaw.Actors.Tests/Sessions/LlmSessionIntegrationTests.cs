// -----------------------------------------------------------------------
// <copyright file="LlmSessionIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Streams;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration test that exercises the full Netclaw actor pipeline:
/// message routing → session actor → IChatClient → strongly-typed output delivery.
/// Subscribers join sessions directly via <see cref="JoinSession"/> and receive
/// <see cref="SessionOutput"/> events filtered by <see cref="OutputFilter"/>.
/// </summary>
public class LlmSessionIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-03-21T12:00:00Z"));
    private readonly RecordingSessionLifecycleObserver _lifecycleObserver = new();
    private readonly ControllableWorkingContextSnapshotProvider _workingContextSnapshots = new();

    protected override bool VerifySerialization => true;

    public LlmSessionIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
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
                DiscoveredToolRetentionTurns = 3,
                DiscoveredToolMaxCount = 12,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<MemoryProposalGate>();
        services.AddSingleton<IMemoryCheckpointSink, NullMemoryCheckpointSink>();
        services.AddSingleton<SQLiteMemoryStore>(sp => new SQLiteMemoryStore(Path.Combine(Path.GetTempPath(), $"netclaw-sidecar-tests-{Guid.NewGuid():N}.db"), TimeProvider.System));
        services.AddSingleton<IMemoryRecallCoordinator>(sp => new SQLiteMemoryRecallCoordinator(
            sp.GetRequiredService<SQLiteMemoryStore>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SQLiteMemoryRecallCoordinator>.Instance));

        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create((string url) => "ok", "navigate_page", "Navigate to URL"),
            "browser_chrome_devtools",
            "navigate_page"));
        registry.Register(new SearchToolsTool(registry));
        registry.Register(new LoadToolTool(registry));

        services.AddSingleton(registry);
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddSingleton<ISessionLifecycleObserver>(_lifecycleObserver);
        services.AddSingleton<IWorkingContextSnapshotProvider>(_workingContextSnapshots);
        services.AddSingleton<ISessionPipeline>(new UnusedSessionPipeline());
    }

    [Fact]
    public async Task JoinSession_receives_SessionJoined_acknowledgement()
    {
        var sessionId = new SessionId("test-channel/join-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("join-probe");

        // Cold-start the session actor OUTSIDE the timed Ask below. The first
        // JoinSession spawns the child through DI (fresh SQLite store, persistence
        // recovery, serialization verification) — unbounded cost on a loaded
        // runner that can exceed the Ask's 3s budget. Warm it first with a guard
        // ceiling; the second JoinSession measures only the hot mailbox path.
        var warmupProbe = CreateTestProbe("join-warmup");
        sessionManager.Tell(new JoinSession(warmupProbe)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        });
        await warmupProbe.ExpectMsgAsync<SessionJoined>(
            TimeSpan.FromSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken);

        var joined = await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, joined.SessionId);
        Assert.Equal(0, joined.TurnCount);
        Assert.Null(joined.Title);
    }

    [Fact]
    public async Task Session_prompt_overlay_is_additive_to_base_system_prompt()
    {
        var sessionId = new SessionId("webhook/prompt-overlay-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("overlay-probe");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new SetSessionPromptOverlay(sessionId)
        {
            PromptOverlay = "Route overlay: triage the webhook payload before deciding whether to notify."
        });

        sessionManager.Tell(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "payload json",
            Source = new MessageSource
            {
                ChannelType = ChannelType.Webhook,
                SenderId = new SenderId("webhook:test"),
                ChannelId = "github-issues",
                MessageId = "delivery-1",
                TurnId = new Netclaw.Actors.Protocol.TurnId("delivery-1"),
                Audience = TrustAudience.Public,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Public),
                Principal = PrincipalClassification.VerifiedAutomation,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
                {
                    SourceKind = new Netclaw.Actors.Channels.SourceKind("issues")
                },
                ReceivedAt = _timeProvider.GetUtcNow()
            }
        });

        await subscriber.ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Base system prompt stays in the System role, but session prompt
        // overlay moved into the volatile User-role tail as part of #608's
        // cache-stability reorder. Search across all message text.
        var allText = string.Join("\n\n", _fakeChatClient.ReceivedMessages.Last()
            .Select(message => message.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        Assert.Contains("You are a test assistant.", allText);
        Assert.Contains("Route overlay: triage the webhook payload before deciding whether to notify.", allText);
    }

    [Fact]
    public async Task Slack_source_rebuilds_system_prompt_with_team_audience_on_first_turn()
    {
        var sessionId = new SessionId("C1234567890/1712700000.000500");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("slack-audience-probe");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello from slack",
            Source = new MessageSource
            {
                ChannelType = ChannelType.Slack,
                SenderId = new SenderId("U123"),
                ChannelId = "C1234567890",
                MessageId = "evt-1",
                TurnId = new Netclaw.Actors.Protocol.TurnId("turn-1"),
                Audience = TrustAudience.Team,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Team),
                Principal = PrincipalClassification.TrustedInternal,
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
                {
                    SourceKind = new Netclaw.Actors.Channels.SourceKind("slack")
                },
                ReceivedAt = _timeProvider.GetUtcNow()
            }
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var allText = string.Join("\n\n", _fakeChatClient.ReceivedMessages.Last()
            .Select(message => message.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        Assert.Contains("You are a test assistant.", allText);
        Assert.DoesNotContain("Public trust context", allText);
    }

    [Fact]
    public async Task SendUserMessage_delivers_TextOutput_and_TurnCompleted()
    {
        var sessionId = new SessionId("test-channel/test-thread");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("adapter-probe");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        var ack = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, Netclaw!"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, ack.SessionId);

        // Subscriber receives typed output events
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, text.SessionId);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, completed.SessionId);
        Assert.Equal(1, completed.TurnNumber.Value);
    }

    [Fact]
    public async Task Repeated_pre_tool_empty_responses_fail_turn_and_allow_followup_prompt()
    {
        // MaxPreToolEmptyRetries is 5, so 6 empty responses: 5 nudged retries + fail.
        for (var i = 0; i < 6; i++)
            _fakeChatClient.PlannedResponses.Enqueue([]);

        var sessionId = new SessionId("test-channel/pre-tool-empty");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("pre-tool-empty-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Please answer"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, error.SessionId);
        Assert.Equal(ErrorCategory.ProviderFailure, error.Category);
        Assert.Contains("Please try rephrasing", error.Message, StringComparison.OrdinalIgnoreCase);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, completed.SessionId);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Try again"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("[fake] Response #7", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Empty_response_after_tool_nudge_fails_turn_and_allows_followup_prompt()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        ];
        // MaxPostToolEmptyRetries is 8, so 9 empty responses: 8 nudged
        // retries + fail.
        for (var i = 0; i < 9; i++)
            _fakeChatClient.PlannedResponses.Enqueue([]);

        var sessionId = new SessionId("test-channel/post-tool-empty");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("post-tool-empty-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, error.SessionId);
        Assert.Equal(ErrorCategory.ProviderFailure, error.Category);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Try again after the failure"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("[fake] Response #11", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delivery_failed_for_latest_turn_retries_once_with_structured_nudge()
    {
        var sessionId = new SessionId("test-channel/delivery-retry");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("delivery-retry-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Say hello"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = completed.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.MessageTooLarge,
            ErrorMessage = "msg_too_long"
        });

        var retried = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Response #2", retried.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

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

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "first"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var firstCompleted = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = firstCompleted.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.ContentRejected,
            ErrorMessage = "invalid_blocks"
        });

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delivery_failed_while_processing_newer_turn_is_ignored()
    {
        var sessionId = new SessionId("test-channel/processing-delivery-failure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("processing-delivery-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "first"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var firstCompleted = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeChatClient.NextResponseGate = gate;

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "second"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = firstCompleted.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.ContentRejected,
            ErrorMessage = "invalid_blocks"
        });

        gate.TrySetResult();

        var secondText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Response #2", secondText.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delivery_retry_budget_stops_after_two_failed_corrections()
    {
        var sessionId = new SessionId("test-channel/delivery-retry-budget");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("delivery-budget-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "first"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

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

            await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        }

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = completed.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.ContentRejected,
            ErrorMessage = "invalid_blocks"
        });

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Transport_failure_injects_nudge_without_triggering_retry()
    {
        var sessionId = new SessionId("test-channel/transport-failure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("transport-failure-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Say hello"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

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
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        // On the next user message, the LLM should see the transport failure nudge
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Are you there?"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

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

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Say hello"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

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
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        // On the next user message, the nudge should be visible in LLM context
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Are you there?"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(_fakeChatClient.ReceivedMessages[^1], msg =>
            msg.Role == Microsoft.Extensions.AI.ChatRole.User
            && msg.Text is not null
            && msg.Text.Contains("unknown delivery error", StringComparison.OrdinalIgnoreCase));
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
        await sessionManager.Ask<SessionJoined>(new JoinSession(textOnlySub)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await textOnlySub.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification
        await sessionManager.Ask<SessionJoined>(new JoinSession(textAndUsageSub)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextAndUsage
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await textAndUsageSub.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification
        await sessionManager.Ask<SessionJoined>(new JoinSession(fullSub)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await fullSub.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Think about this"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // TextOnly: TextOutput + TurnCompleted (lifecycle always delivered)
        await textOnlySub.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await textOnlySub.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await textOnlySub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200), cancellationToken: TestContext.Current.CancellationToken);

        // TextAndUsage: TextOutput + UsageOutput + TurnCompleted (no thinking)
        await textAndUsageSub.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await textAndUsageSub.ExpectMsgAsync<UsageOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await textAndUsageSub.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await textAndUsageSub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200), cancellationToken: TestContext.Current.CancellationToken);

        // Full: ThinkingOutput + TextOutput + UsageOutput + TurnCompleted
        await fullSub.ExpectMsgAsync<ThinkingOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await fullSub.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var usage = await fullSub.ExpectMsgAsync<UsageOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(128_000, usage.ContextWindowTokens);
        Assert.NotNull(usage.UsagePercent);
        Assert.True(usage.UsagePercent > 0);
        await fullSub.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await fullSub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Two_sessions_are_routed_independently()
    {
        var session1 = new SessionId("channel-A/thread-1");
        var session2 = new SessionId("channel-B/thread-2");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var sub1 = CreateTestProbe("adapter-1");
        var sub2 = CreateTestProbe("adapter-2");

        await sessionManager.Ask<SessionJoined>(new JoinSession(sub1)
        {
            SessionId = session1,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await sub1.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification
        await sessionManager.Ask<SessionJoined>(new JoinSession(sub2)
        {
            SessionId = session2,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await sub2.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session1,
            Content = "Message for session 1",
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session2,
            Content = "Message for session 2"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Each subscriber only gets its own session's output
        var text1 = await sub1.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(session1, text1.SessionId);
        await sub1.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        var text2 = await sub2.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(session2, text2.SessionId);
        await sub2.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        await sub1.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200), cancellationToken: TestContext.Current.CancellationToken);
        await sub2.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Buffered_messages_are_batched_into_follow_up_LLM_call()
    {
        _fakeChatClient.Delay = TimeSpan.FromMilliseconds(200);
        var sessionId = new SessionId("channel-C/thread-3");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("adapter-batch");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        // First message — actor enters Processing
        var ack1 = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, ack1.SessionId);

        // These two are deterministically buffered
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Third message"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // First turn output
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);

        // Second turn output (batched follow-up)
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);

        // Only two LLM calls total
        Assert.Equal(2, _fakeChatClient.CallCount);

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Discovered_tools_are_retained_then_expire_after_lease_window()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-load", "load_tool",
                new Dictionary<string, object?> { ["Name"] = "browser_chrome_devtools/navigate_page" })
        ];

        _fakeToolExecutor.Results["load_tool"] = "browser_chrome_devtools/navigate_page";

        var sessionId = new SessionId("channel-discovery/thread-retention");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("discovery-retention-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Find browser tools"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 4; i++)
        {
            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = $"Follow-up turn {i + 1}"
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

            await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
            await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.True(_fakeChatClient.ReceivedToolNames.Count >= 6);

        // ReceivedToolNames records the AIFunction.Name surfaced to the LLM,
        // which for MCP tools is the Anthropic-safe sanitized alias
        // (server__tool), not the canonical server/tool form.
        Assert.Contains("browser_chrome_devtools__navigate_page", _fakeChatClient.ReceivedToolNames[2]);
        Assert.Contains("browser_chrome_devtools__navigate_page", _fakeChatClient.ReceivedToolNames[3]);
        Assert.Contains("browser_chrome_devtools__navigate_page", _fakeChatClient.ReceivedToolNames[4]);
        Assert.DoesNotContain("browser_chrome_devtools__navigate_page", _fakeChatClient.ReceivedToolNames[5]);
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

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Preamble text should arrive before tool calls
        var preamble = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Let me search for that...", preamble.Text);

        // BufferFlush should arrive after preamble text
        await subscriber.ExpectMsgAsync<BufferFlush>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Then tool call
        var toolCall = await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("browser_chrome_devtools/navigate_page", toolCall.ToolName.Value);

        // Drain tool result output emitted after tool execution
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // After tool execution and follow-up LLM call, final text response
        var finalText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", finalText.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
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
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Text | OutputFilter.ToolCalls
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Do something with tools"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Should receive exactly ONE consolidated TextOutput for the preamble
        var preamble = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Part one", preamble.Text);
        Assert.Contains("Part two", preamble.Text);

        // Tool call output
        var toolCall = await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("browser_chrome_devtools/navigate_page", toolCall.ToolName.Value);

        // Drain tool result output emitted after tool execution
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Final text response after tool execution
        var finalText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", finalText.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Session_recovers_state_after_actor_is_killed()
    {
        var sessionId = new SessionId("test-channel/recovery-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("recovery-sub");

        // Phase 1: Build up state — two completed turns
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Phase 2: Kill the session actor child
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);

        // Phase 3: Recover — send JoinSession to the same session ID.
        // GenericChildPerEntityParent creates a new actor that recovers from journal.
        var recoverSub = CreateTestProbe("recovery-sub-2");
        var recovered = await sessionManager.Ask<SessionJoined>(new JoinSession(recoverSub)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await recoverSub.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification
        Assert.Equal(sessionId, recovered.SessionId);
        Assert.Equal(2, recovered.TurnCount); // Both turns recovered

        // Phase 4: Verify the session still works — send a third message
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Third message after recovery"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var text = await recoverSub.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        var completed = await recoverSub.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, completed.TurnNumber.Value); // Continues from recovered state
    }

    [Fact]
    public async Task Rejoin_suppresses_duplicate_SessionJoined_on_subscriber()
    {
        var sessionId = new SessionId("test-channel/rejoin-suppress");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("rejoin-sub");

        // First join — subscriber receives SessionJoined
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Re-join via Tell (piggybacked path) — subscriber should NOT get a duplicate
        sessionManager.Tell(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, ActorRefs.NoSender);

        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        // Re-join via Ask still returns SessionJoined to the caller (not the subscriber)
        var rejoined = await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, rejoined.SessionId);

        // Subscriber still should not have received a duplicate
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Session_does_not_passivate_with_active_subscribers()
    {
        var sessionId = new SessionId("test-channel/no-passivate-sub");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("active-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Resolve session actor
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        // Send ReceiveTimeout directly — with active subscriber, should be deferred
        child.Tell(ReceiveTimeout.Instance);

        // Actor should still be alive
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        // Session should still process messages
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Still alive?"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Session_idle_timeout_deactivates_only_when_actor_passivates()
    {
        var sessionId = new SessionId("test-channel/deactivate-on-passivate");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("deactivate-passivate-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        child.Tell(ReceiveTimeout.Instance);
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);

        child.Tell(new LeaveSession(subscriber)
        {
            SessionId = sessionId
        });

        child.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }

    [Fact]
    public async Task Ready_session_drains_for_daemon_restart()
    {
        var sessionId = new SessionId("test-channel/restart-drain-ready");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("restart-drain-ready-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}").ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        var ack = await sessionManager.Ask<CommandAck>(new PrepareForDaemonRestart(sessionId, "config-reload"), TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(sessionId, ack.SessionId);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
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

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}").ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        var drainTask = sessionManager.Ask<CommandAck>(new PrepareForDaemonRestart(sessionId, "config-reload"), TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var nack = await sessionManager.Ask<CommandNack>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Should be rejected"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SessionIngressGate.RestartInProgressMessage, nack.Reason);

        sessionManager.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(999),
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.MessageTooLarge,
            ErrorMessage = "msg_too_long"
        });

        responseGate.TrySetResult();

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, (await drainTask).SessionId);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
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

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Trigger compaction"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}").ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        var drainTask = sessionManager.Ask<CommandAck>(new PrepareForDaemonRestart(sessionId, "config-reload"), TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var nack = await sessionManager.Ask<CommandNack>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Should be rejected during compaction"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SessionIngressGate.RestartInProgressMessage, nack.Reason);

        child.Tell(new CompactionFailed
        {
            Cause = new InvalidOperationException("test compaction completion")
        });

        Assert.Equal(sessionId, (await drainTask).SessionId);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }

    [Fact]
    public async Task Warm_session_injects_restart_notice_on_next_turn()
    {
        var sessionId = new SessionId("test-channel/restart-warm-notice");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("restart-warm-notice-sub");

        await sessionManager.Ask<CommandAck>(new WarmSession(sessionId, "The daemon restarted due to a configuration change. Recovery resumed from the last durable checkpoint."), TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello after restart"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Turn restart notice moved from System-role to the volatile
        // User-role tail as part of #608's cache-stability reorder.
        Assert.Contains(_fakeChatClient.ReceivedMessages, conversation =>
            conversation.Any(msg =>
                msg.Text is not null
                && msg.Text.Contains("Recovery resumed from the last durable checkpoint", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Working_context_provider_failure_is_nonfatal_and_starts_the_llm_call()
    {
        _workingContextSnapshots.Failure = new IOException("credential-bearing provider failure");
        var sessionId = new SessionId("console/working-context-failure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("working-context-failure-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Continue despite context inspection failure"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var prompt = string.Join('\n', _fakeChatClient.ReceivedMessages.Single().Select(message => message.Text));
        Assert.Contains("status: unavailable", prompt);
        Assert.Contains("working context inspection failed", prompt);
        Assert.DoesNotContain("credential-bearing", prompt);
    }

    [Fact]
    public async Task Cancelled_turn_discards_stale_working_context_continuation()
    {
        _workingContextSnapshots.Pending = new TaskCompletionSource<WorkingContextSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionId = new SessionId("test-channel/stale-working-context");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("stale-working-context-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Start a turn whose context inspection is delayed"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await _workingContextSnapshots.InvocationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        child.Tell(new ToolExecutionFailed { Cause = new IOException("cancel the pending turn") });

        await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        child.Tell(new WorkingContextSnapshotReady(
            Generation: 1,
            ForceNoTools: false,
            TurnRestartNotice: null,
            Snapshot: new WorkingContextSnapshot
            {
                WorkingContext = WorkingContext.Empty.WithProjectDirectory("/stale/project"),
                Git = new GitWorkingContextInspection.NotRepository()
            }), TestActor);
        child.Tell(new JoinSession(TestActor)
        {
            SessionId = sessionId,
            Filter = OutputFilter.None
        }, TestActor);
        await ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(_fakeChatClient.ReceivedMessages);
        _workingContextSnapshots.Pending.TrySetCanceled(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Subscriber_survives_session_actor_passivation_via_piggyback_rejoin()
    {
        var sessionId = new SessionId("test-channel/passivation-rejoin");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriberA = CreateTestProbe("sub-a");

        // Phase 1: Join with subscriber A, complete a turn
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberA)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberA.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberA.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberA.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Phase 2: Kill session actor (simulating passivation)
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);

        // Phase 3: Join with subscriber B (triggers re-creation)
        var subscriberB = CreateTestProbe("sub-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Phase 4: Piggybacked JoinSession for A + SendUserMessage
        // This simulates what ChannelPipeline does on each inbound message
        sessionManager.Tell(new JoinSession(subscriberA)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, ActorRefs.NoSender);

        await subscriberA.ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "After passivation"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // Both subscribers should receive output
        await subscriberA.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberA.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriberB.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task New_user_message_during_passivation_aborts_shutdown_and_processes_message()
    {
        var sessionId = new SessionId("test-channel/passivation-new-message");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("passivation-new-message-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        child.Tell(new LeaveSession(subscriber)
        {
            SessionId = sessionId
        });

        child.Tell(ReceiveTimeout.Instance);

        var ack = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Interrupt passivation"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(sessionId, ack.SessionId);

        // Poll until the turn is fully persisted. Checking CallCount alone is not
        // sufficient — the in-memory journal persists asynchronously, so TurnCount
        // can still be 0 at the moment CallCount first reaches 1. CallCount is
        // intentionally NOT asserted inside the retry loop: if retries push it above
        // the expected value, a strict equality check would loop forever rather than
        // failing fast. Assert it once after the loop, when the actor is idle.
        // JoinSession is idempotent for the same subscriber (Dictionary keyed by
        // IActorRef), so repeated calls with the witness probe are safe.
        var witness = CreateTestProbe("passivation-witness");
        await AwaitAssertAsync(async () =>
        {
            var peek = await sessionManager.Ask<SessionJoined>(new JoinSession(witness)
            {
                SessionId = sessionId,
                Filter = OutputFilter.TextOnly
            }, TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, peek.TurnCount);
        }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, _fakeChatClient.CallCount);

        var rejoined = await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, rejoined.TurnCount);
        await subscriber.ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }

    [Fact]
    public async Task Delivery_feedback_during_passivation_aborts_shutdown_and_retries_latest_turn()
    {
        var sessionId = new SessionId("test-channel/passivation-delivery-feedback");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("passivation-delivery-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Original message"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        // Subscribe a fresh probe to observe the retried turn deterministically.
        // It is registered while the actor is Passivating — JoinSession is handled
        // there (CommandSubscriptionMessages) without aborting passivation — so the
        // probe is in the subscriber set before the retry turn emits any output.
        var retryWatcher = CreateTestProbe("passivation-retry-watch");

        child.Tell(new LeaveSession(subscriber)
        {
            SessionId = sessionId
        });

        // These four messages are consecutive Tells to the child from this
        // thread, so they enqueue into its mailbox in call order and are
        // processed ahead of any cross-actor message: LeaveSession (drop the
        // subscriber) -> ReceiveTimeout (enter Passivating) -> JoinSession
        // (subscribe retryWatcher, no abort) -> DeliveryFailed (abort
        // passivation, retry the latest turn). Sent directly to the child, not
        // via sessionManager, so the feedback lands during Passivating rather
        // than after the actor has stopped and been re-created by the parent.
        child.Tell(ReceiveTimeout.Instance);

        child.Tell(
            new JoinSession(retryWatcher)
            {
                SessionId = sessionId,
                Filter = OutputFilter.TextOnly
            },
            retryWatcher);

        child.Tell(new DeliveryFailed
        {
            SessionId = sessionId,
            TurnNumber = completed.TurnNumber,
            ChannelType = ChannelType.Slack,
            FailureKind = DeliveryFailureKind.MessageTooLarge,
            ErrorMessage = "msg_too_long"
        });

        // retryWatcher was subscribed before the retry began, so it
        // deterministically receives the retried turn's output — no polling of
        // fake-client internals against a fixed budget.
        await retryWatcher.ExpectMsgAsync<SessionJoined>(
            TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await retryWatcher.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var retriedTurn = await retryWatcher.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(completed.TurnNumber.Value + 1, retriedTurn.TurnNumber.Value);

        var rejoined = await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, rejoined.TurnCount);
        await subscriber.ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        // The retried turn must carry the delivery-failure feedback to the LLM.
        // Checked once the session is quiescent (turn 2 completed, no call in
        // flight) so ReceivedMessages is not being mutated by a concurrent call.
        Assert.True(_fakeChatClient.CallCount >= 2);
        Assert.Contains(_fakeChatClient.ReceivedMessages, conversation =>
            conversation.Any(msg =>
                msg.Role == Microsoft.Extensions.AI.ChatRole.User
                && msg.Text is not null
                && msg.Text.Contains("msg_too_long", StringComparison.OrdinalIgnoreCase)));

        Assert.DoesNotContain(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }

    // NOTE: transient-failure retry (including ProviderException 5xx recover/exhaust) moved
    // from this actor to the transport RetryingChatClient and is covered by
    // RetryingChatClientTests; the actor no longer retries, so the former
    // Streaming_retry_recovers_from_transient_502 / _exhaustion_fails_turn integration tests
    // were removed.

    [Fact]
    public async Task Reminder_redelivery_is_deduped_in_Ready_phase()
    {
        var sessionId = new SessionId("dedup-ready/thread");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("dedup-ready-probe");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var reminderSource = ReminderSource("check-pr:1712000000000");

        // First delivery: full turn runs.
        var firstAck = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Check PR #123 again",
            Source = reminderSource
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, firstAck.SessionId);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var callsAfterFirst = _fakeChatClient.ReceivedMessages.Count;

        // Second delivery with same ReminderId: must return CommandAck
        // from the dedup pre-check without invoking the LLM again.
        var dupAck = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Check PR #123 again",
            Source = reminderSource
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, dupAck.SessionId);

        await AwaitAssertAsync(() =>
        {
            Assert.Equal(callsAfterFirst, _fakeChatClient.ReceivedMessages.Count);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reminder_redelivery_is_deduped_while_first_turn_is_in_flight()
    {
        var sessionId = new SessionId("dedup-inflight/thread");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("dedup-inflight-probe");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var reminderSource = ReminderSource("check-pr:1712000000001");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeChatClient.NextResponseGate = gate;

        var firstAck = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Check PR #123 again",
            Source = reminderSource
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, firstAck.SessionId);

        // The CommandAck fires BEFORE the first turn's LLM call is scheduled —
        // recall resolution and a working-context mailbox hop run first — so
        // polling CallCount races the very thing it measures. FirstCallEntered
        // completes the instant the call enters GetResponseAsync, while the
        // gate keeps it blocked: that is the deterministic "in flight" proof.
        // The 30s ceiling is a hang guard only, not the pass/fail mechanism.
        await _fakeChatClient.FirstCallEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, _fakeChatClient.CallCount);

        var callsWhileBlocked = _fakeChatClient.CallCount;

        var duplicateAck = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Check PR #123 again",
            Source = reminderSource
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, duplicateAck.SessionId);

        await AwaitAssertAsync(() =>
        {
            Assert.Equal(callsWhileBlocked, _fakeChatClient.CallCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);

        gate.TrySetResult();

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
        {
            Assert.Equal(callsWhileBlocked, _fakeChatClient.CallCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Non_reminder_messages_bypass_dedup()
    {
        var sessionId = new SessionId("dedup-bypass/thread");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("dedup-bypass-probe");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Prime the dedup set with a completed reminder turn.
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Reminder prompt",
            Source = ReminderSource("cron:42")
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // A regular user message (ReminderId = null) must always be processed.
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Normal follow-up question"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    private MessageSource ReminderSource(string reminderId) => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId("reminder-system"),
        ChannelId = null,
        Audience = TrustAudience.Personal,
        Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        {
            SourceKind = new Netclaw.Actors.Channels.SourceKind("reminder")
        },
        ReceivedAt = _timeProvider.GetUtcNow(),
        ReminderId = new ReminderId(reminderId)
    };
}

/// <summary>
/// Fake IChatClient that returns canned responses for testing.
/// Supports configurable thinking tokens, usage data, and tool calls.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => _callCount;

    // Completes the first time GetResponseAsync is entered (after CallCount is
    // incremented, before any delay/gate). Lets a test prove a turn is genuinely
    // "in flight" deterministically instead of polling CallCount, which races the
    // actor's ack-then-call ordering. One instance per test, so no reset needed.
    public TaskCompletionSource FirstCallEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // GetResponseAsync is invoked concurrently from multiple actor dispatcher / ThreadPool
    // threads on one shared instance (main-model turn + compaction summarizer sidecar +
    // fire-and-forget memory-extraction sidecar), while test threads enumerate and index the
    // recorded collections. This gate serializes every append below, and the public getters
    // return a snapshot copy taken under it. Locking the writes alone is NOT sufficient: a
    // caller's List<T> enumeration still throws "Collection was modified" if an Add interleaves
    // its MoveNext, so the read side must copy under the same lock. Never held across an await.
    private readonly object _recordingGate = new();

    private readonly List<IReadOnlyList<ChatMessage>> _receivedMessages = [];
    private readonly List<IReadOnlyList<string>> _receivedToolNames = [];
    private readonly List<ChatOptions?> _receivedOptions = [];

    /// <summary>Snapshot copy taken under <see cref="_recordingGate"/> so a test can safely
    /// enumerate / index / LINQ over it while actor threads keep appending.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> ReceivedMessages
    {
        get { lock (_recordingGate) { return _receivedMessages.ToArray(); } }
    }

    /// <summary>Snapshot copy taken under <see cref="_recordingGate"/>; see <see cref="ReceivedMessages"/>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ReceivedToolNames
    {
        get { lock (_recordingGate) { return _receivedToolNames.ToArray(); } }
    }

    /// <summary>The <see cref="ChatOptions"/> object passed on each call, so tests can
    /// assert the session actor threads a <c>SessionScopedChatOptions</c> carrier through
    /// for per-session log correlation. Snapshot copy taken under <see cref="_recordingGate"/>.</summary>
    public IReadOnlyList<ChatOptions?> ReceivedOptions
    {
        get { lock (_recordingGate) { return _receivedOptions.ToArray(); } }
    }

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
    /// When populated, tool-call decisions are dequeued per main-model response
    /// before falling back to <see cref="AlwaysReturnToolCalls"/> or the first-call behavior.
    /// Observation compressor and sidecar calls do not consume this queue.
    /// </summary>
    public Queue<bool> PlannedToolCallDecisions { get; } = new();

    /// <summary>
    /// When populated, usage details are dequeued per main-model response before
    /// falling back to <see cref="UsageOverride"/>. Observation compressor and
    /// sidecar calls do not consume this queue.
    /// </summary>
    public Queue<UsageDetails?> PlannedUsageOverrides { get; } = new();

    /// <summary>
    /// Number of compaction observation sidecar calls that should hang until cancellation.
    /// Used to simulate providers that never return during compaction.
    /// </summary>
    public int HangingObservationCallsRemaining { get; set; }

    /// <summary>
    /// When set, the observer sidecar call returns this text instead of the
    /// default <c>"- compacted observation"</c>. Used by tests that need the
    /// observer output to look like a real structured summary so successive
    /// compactions can be exercised.
    /// </summary>
    public string? ObservationResponseOverride { get; set; }

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

    /// <summary>
    /// When populated, exceptions are dequeued and thrown before processing the
    /// response. Used to simulate provider errors (502, 400 context overflow, etc.).
    /// Exceptions are thrown before incrementing <see cref="CallCount"/>.
    /// </summary>
    public Queue<Exception> PlannedExceptions { get; } = new();

    public TaskCompletionSource? NextResponseGate { get; set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialize the caller's own enumerables outside the lock — they are private to this
        // call and can be non-trivial to enumerate; only the shared-list appends need guarding.
        var messageList = messages.ToList();
        var toolNames = options?.Tools?
            .Select(t => t is AIFunction f ? f.Name : t.GetType().Name)
            .ToList()
            ?? [];

        lock (_recordingGate)
        {
            // Consume a planned exception before recording so a failed call is not counted
            // (CallCount / recorded-messages contract). Every call path — main model and both
            // sidecars — reaches this on a different thread, so the check+dequeue must be guarded.
            if (PlannedExceptions.Count > 0)
                throw PlannedExceptions.Dequeue();

            _receivedMessages.Add(messageList);
            _receivedOptions.Add(options);
            _receivedToolNames.Add(toolNames);
        }
        if (Interlocked.Increment(ref _callCount) == 1)
            FirstCallEntered.TrySetResult();

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

        if (systemText.Contains("You are a session summarizer", StringComparison.Ordinal))
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
                ObservationResponseOverride ?? "- compacted observation"));
        }

        var plannedToolCallDecision = PlannedToolCallDecisions.Count > 0
            ? PlannedToolCallDecisions.Dequeue()
            : (bool?)null;
        var usageOverride = PlannedUsageOverrides.Count > 0
            ? PlannedUsageOverrides.Dequeue()
            : UsageOverride;

        // Return tool calls if configured
        if (ToolCallsOnFirstCall is not null)
        {
            var returnToolCalls = plannedToolCallDecision ?? (AlwaysReturnToolCalls
                ? (IgnoreToolAvailability || options?.Tools?.Count > 0)   // Every call, even when tools are withheld
                : _callCount == 1);            // First call only (existing behavior)

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
                if (usageOverride is not null)
                    toolResponse.Usage = usageOverride;
                return toolResponse;
            }
        }

        if (PlannedResponses.Count > 0)
        {
            var plannedContents = PlannedResponses.Dequeue();
            var plannedResponse = new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                new List<AIContent>(plannedContents)));
            if (usageOverride is not null)
                plannedResponse.Usage = usageOverride;
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

        if (usageOverride is not null)
        {
            response.Usage = usageOverride;
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

internal sealed class ControllableWorkingContextSnapshotProvider : IWorkingContextSnapshotProvider
{
    public Exception? Failure { get; set; }
    public TaskCompletionSource<WorkingContextSnapshot>? Pending { get; set; }
    public TaskCompletionSource InvocationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<WorkingContextSnapshot> CreateAsync(
        WorkingContext context,
        TrustAudience audience,
        CancellationToken cancellationToken)
    {
        InvocationStarted.TrySetResult();
        if (Failure is { } failure)
            return Task.FromException<WorkingContextSnapshot>(failure);
        if (Pending is { } pending)
            return pending.Task;

        return Task.FromResult(new WorkingContextSnapshot
        {
            WorkingContext = context,
            Git = new GitWorkingContextInspection.Skipped()
        });
    }
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

    public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
        => Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
}
