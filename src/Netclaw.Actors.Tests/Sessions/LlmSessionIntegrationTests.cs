// -----------------------------------------------------------------------
// <copyright file="LlmSessionIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Streams;
using Akka.TestKit;
using System.Collections.Concurrent;
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
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Tests.Utilities;
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
    private ToolRegistry _toolRegistry = null!;
    private ToolConfig _toolConfig = null!;

    protected override bool VerifySerialization => true;

    public LlmSessionIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void DeleteOwnedDirectories()
    {
        // The base class deletes TestPaths after this hook.
        SqliteMemoryStoreTestPools.ClearOwnedDirectory(TestPaths.BasePath);
    }

    [Fact]
    public async Task Duplicate_source_message_is_acknowledged_without_a_second_turn()
    {
        var sessionId = new SessionId("admission/journal-before-ack");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("admission-subscriber");
        await JoinSessionAsync(manager, subscriber, sessionId);

        var source = new MessageSource
        {
            ChannelType = ChannelType.SignalR,
            SenderId = new SenderId("operator-1"),
            ChannelId = "operator-channel",
            MessageId = "source-event-1",
            TurnId = new Netclaw.Actors.Protocol.TurnId("source-turn-1"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
            ReceivedAt = _timeProvider.GetUtcNow()
        };

        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Finish the operator task",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        var callCount = _fakeChatClient.CallCount;

        var duplicateAck = await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Finish the operator task",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(sessionId, duplicateAck.SessionId);
        await subscriber.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
        Assert.Equal(callCount, _fakeChatClient.CallCount);
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
        services.AddSingleton<SQLiteMemoryStore>(sp => new SQLiteMemoryStore(Path.Combine(TestPaths.BasePath, "sidecar-memory.db"), TimeProvider.System));
        services.AddSingleton<IMemoryRecallCoordinator>(sp => new SQLiteMemoryRecallCoordinator(
            sp.GetRequiredService<SQLiteMemoryStore>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            sp.GetRequiredService<TimeProvider>()));

        var registry = new ToolRegistry();
        _toolRegistry = registry;
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create((string url) => "ok", "navigate_page", "Navigate to URL"),
            "browser_chrome_devtools",
            "navigate_page"));
        _toolConfig = new ToolConfig();
        _toolConfig.AudienceProfiles.Public.AllowedMcpServers.Add("browser_chrome_devtools");
        _toolConfig.AudienceProfiles.Public.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["browser_chrome_devtools"] = ["navigate_page"]
        };
        var toolAccessPolicy = TestToolAccessPolicy.Create(_toolConfig);
        registry.RegisterCore(new SearchToolsTool(registry, toolAccessPolicy));
        registry.RegisterCore(new LoadToolTool(registry, toolAccessPolicy));

        services.AddSingleton(registry);
        services.AddSingleton(toolAccessPolicy);
        // Tool exposure needs a resolved trust context. A Public deployment
        // ceiling keeps the Public exposure that these tests assert.
        services.AddSingleton(new TrustContextDeriver(new EffectivePolicyDefaults(
            DeploymentPosture.Public,
            TrustAudience.Public,
            ShellExecutionMode.Off,
            UsedStrictFallback: true)));
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
    public async Task Session_emits_one_payload_free_tool_exposure_diagnostic()
    {
        var sessionId = new SessionId("signalr/tool-exposure-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("tool-exposure-probe");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);

        await EventFilter.Info(
                message: "Session tool exposure core=2 deferredVisible=1 loaded=0")
            .ExpectAsync(1, async () =>
            {
                await sessionManager.Ask<CommandAck>(new SendUserMessage
                {
                    SessionId = sessionId,
                    Content = "private-payload-marker",
                    Source = new MessageSource
                    {
                        ChannelType = ChannelType.SignalR,
                        SenderId = new SenderId("synthetic-operator"),
                        ChannelId = "synthetic-channel",
                        MessageId = "synthetic-message",
                        TurnId = new Netclaw.Actors.Protocol.TurnId("synthetic-turn"),
                        Audience = TrustAudience.Personal,
                        Boundary = TrustBoundary.Personal,
                        Principal = PrincipalClassification.Operator,
                        Provenance = new SourceProvenance(
                            TransportAuthenticity.Verified,
                            PayloadTaint.Trusted),
                        ReceivedAt = _timeProvider.GetUtcNow()
                    }
                }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

                await subscriber.ExpectMsgAsync<TextOutput>(
                    TimeSpan.FromSeconds(3),
                    cancellationToken: TestContext.Current.CancellationToken);
                await subscriber.ExpectMsgAsync<TurnCompleted>(
                    TimeSpan.FromSeconds(3),
                    cancellationToken: TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);
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
        // The stream finishes with Stop. Only then do repeated empty replies blame the model.
        _fakeChatClient.PlannedFinishReason = ChatFinishReason.Stop;
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
        Assert.Equal(ErrorCategory.ModelBehavior, error.Category);
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
        // retries + fail. The stream finishes with Stop, so the model is to blame.
        _fakeChatClient.PlannedFinishReason = ChatFinishReason.Stop;
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
        Assert.Equal(ErrorCategory.ModelBehavior, error.Category);

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

        await JoinSessionAsync(sessionManager, subscriber, sessionId);

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

        await JoinSessionAsync(sessionManager, subscriber, sessionId);

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

        await JoinSessionAsync(sessionManager, subscriber, sessionId);

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

        await JoinSessionAsync(sessionManager, subscriber, sessionId);

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

        await JoinSessionAsync(sessionManager, subscriber, sessionId);

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

        await JoinSessionAsync(sessionManager, subscriber, sessionId);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Buffered_messages_are_batched_into_follow_up_LLM_call(bool mixedAuthority)
    {
        // Arrange
        var firstResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeChatClient.NextResponseGate = firstResponseGate;
        var sessionId = new SessionId("channel-C/thread-3");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("adapter-batch");
        (string Content, TrustAudience Audience)[] queued = mixedAuthority
            ?
            [
                ("Personal buffer one", TrustAudience.Personal),
                ("Public buffer", TrustAudience.Public),
                ("Personal buffer two", TrustAudience.Personal)
            ]
            :
            [
                ("Second message", TrustAudience.Personal),
                ("Third message", TrustAudience.Personal)
            ];
        // One turn has one requester. Inputs from different requesters never share a follow-up call, so the
        // mixed case runs each run of same-requester inputs as its own turn, in admission order.
        string[][] followUpBatches = mixedAuthority
            ? [["Personal buffer one"], ["Public buffer"], ["Personal buffer two"]]
            : [["Second message", "Third message"]];

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken); // Drain subscriber notification

        // Act
        try
        {
            var ack1 = await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "First message",
                Source = mixedAuthority ? SourceFor("first", TrustAudience.Personal) : null
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(sessionId, ack1.SessionId);
            await _fakeChatClient.FirstCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            foreach (var (content, audience) in queued)
            {
                await sessionManager.Ask<CommandAck>(new SendUserMessage
                {
                    SessionId = sessionId,
                    Content = content,
                    Source = mixedAuthority ? SourceFor(content.Replace(' ', '-'), audience) : null
                }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            }
            firstResponseGate.TrySetResult();

            for (var turn = 0; turn <= followUpBatches.Length; turn++)
            {
                await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
                await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
            }

            // Assert
            Assert.Equal(1 + followUpBatches.Length, _fakeChatClient.CallCount);
            for (var call = 1; call <= followUpBatches.Length; call++)
            {
                var bufferedInputs = _fakeChatClient.ReceivedMessages[call]
                    .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                        && queued.Any(input => input.Content == message.Text))
                    .Select(message => message.Text)
                    .ToArray();
                Assert.Equal(followUpBatches.Take(call).SelectMany(static batch => batch), bufferedInputs);
            }

            await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            firstResponseGate.TrySetResult();
        }

        MessageSource SourceFor(string messageId, TrustAudience audience) => new()
        {
            ChannelType = audience == TrustAudience.Public ? ChannelType.Webhook : ChannelType.SignalR,
            SenderId = new SenderId(audience == TrustAudience.Public ? "webhook:test" : "operator-1"),
            ChannelId = audience == TrustAudience.Public ? "webhook-channel" : "operator-channel",
            MessageId = messageId,
            TurnId = new Netclaw.Actors.Protocol.TurnId($"turn-{messageId}"),
            Audience = audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
            Principal = audience == TrustAudience.Public
                ? PrincipalClassification.VerifiedAutomation : PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified,
                audience == TrustAudience.Public ? PayloadTaint.Public : PayloadTaint.Trusted),
            ReceivedAt = _timeProvider.GetUtcNow()
        };
    }

    [Fact]
    public async Task Initial_model_call_contains_core_tools_but_not_deferred_tools()
    {
        var sessionId = new SessionId("channel-discovery/core-contract");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("core-contract-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use the initial tool set"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(_fakeChatClient.ReceivedToolNames);
        Assert.Equal(
            ["load_tool", "search_tools"],
            _fakeChatClient.ReceivedToolNames[0].OrderBy(static name => name, StringComparer.Ordinal));
        Assert.DoesNotContain("browser_chrome_devtools__navigate_page", _fakeChatClient.ReceivedToolNames[0]);
    }

    // The nudge that follows a repeated refusal. The model reads it. A person never sees it.
    private const string RepeatNudgeStart = "[system: Netclaw refused your repeated call to `search_tools` again.";
    private const string LongRunningCommandsPage =
        "skill_read_resource('netclaw-operations', 'references/long-running-commands.md')";

    private static bool IsRepeatNudge(ChatMessage message)
        => message.Role == Microsoft.Extensions.AI.ChatRole.User
            && message.Text.StartsWith(RepeatNudgeStart, StringComparison.Ordinal);

    private static FunctionCallContent SearchCall(string callId, string query)
        => new(callId, "search_tools", new Dictionary<string, object?> { ["query"] = query });

    [Fact]
    public async Task Repeated_refused_call_is_refused_again_with_a_nudge_and_the_turn_continues()
    {
        // Live example, 18 Oct 2026: a command timed out twice, the third run was refused, and the
        // model sent it again. Aaron's rule: refuse it again, nudge the model, and keep the turn going.
        var diagnostics = CreateTestProbe();
        Sys.EventStream.Subscribe(diagnostics, typeof(Warning));
        for (var round = 1; round <= 4; round++)
            _fakeChatClient.PlannedResponses.Enqueue([SearchCall($"cycle-call-{round}", "browser")]);
        // After the nudge the model takes another action. That action runs.
        _fakeChatClient.PlannedResponses.Enqueue([SearchCall("alternative-call", "alternative")]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("The browser search did not change, so I tried another query.")]);
        _fakeToolExecutor.Results["search_tools"] = "same result";

        var sessionId = new SessionId("channel-cycle/repeat-nudge");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("repeat-nudge-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Repeat the same search."
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var outputs = new List<object>();
        await subscriber.FishForMessageAsync<object>(message =>
        {
            outputs.Add(message);
            return message is TurnCompleted;
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // The person sees no error. The turn completes with the model's own text.
        Assert.DoesNotContain(outputs, static output => output is ErrorOutput);
        Assert.Equal(TurnOutcome.Completed, Assert.IsType<TurnCompleted>(outputs[^1]).Outcome);
        var texts = outputs.OfType<TextOutput>().Select(static output => output.Text).ToList();
        Assert.Equal(["The browser search did not change, so I tried another query."], texts);
        Assert.DoesNotContain(texts, static text => text.Contains("Netclaw stopped", StringComparison.Ordinal));

        var results = outputs.OfType<ToolResultOutput>().ToList();
        Assert.Equal(5, results.Count);
        Assert.Equal("same result", results[0].Result);
        Assert.Equal("same result", results[1].Result);
        foreach (var refused in results.Skip(2).Take(2))
        {
            Assert.Contains("repeated action-and-outcome cycle", refused.Result, StringComparison.Ordinal);
            Assert.Contains("This call did not execute.", refused.Result, StringComparison.Ordinal);
        }

        Assert.Equal("alternative-call", results[4].CallId.Value);
        Assert.Equal("same result", results[4].Result);
        // Two executed rounds and the alternative. Neither refused call ran.
        Assert.Equal(3, _fakeToolExecutor.CallCount);
        Assert.Equal(6, _fakeChatClient.CallCount);

        var requests = _fakeChatClient.ReceivedMessages;
        // The first correction has no nudge. The repeat after it gets one, as the last message.
        Assert.DoesNotContain(requests[3], IsRepeatNudge);
        var nudge = requests[4][^1];
        Assert.True(IsRepeatNudge(nudge), $"The last message was: {nudge.Text}");
        Assert.Contains(LongRunningCommandsPage, nudge.Text, StringComparison.Ordinal);
        Assert.Contains("_background: true", nudge.Text, StringComparison.Ordinal);
        Assert.Contains("check_background_job", nudge.Text, StringComparison.Ordinal);
        Assert.Contains("did not execute", nudge.Text, StringComparison.Ordinal);
        Assert.Single(requests[5], IsRepeatNudge);

        for (var i = 0; i < 2; i++)
        {
            var diagnostic = await diagnostics.FishForMessageAsync<Warning>(
                warning => warning.Message.ToString()!.StartsWith("Tool recurrence decision", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(typeof(TurnStateTracker), diagnostic.LogClass);
            Assert.DoesNotContain(sessionId.Value, diagnostic.LogSource, StringComparison.Ordinal);
            Assert.Equal($"TurnStateTracker (akka://{Sys.Name})", diagnostic.LogSource);
            var properties = Assert.IsAssignableFrom<LogMessage>(diagnostic.Message).GetProperties();
            Assert.Equal(["DecisionKind", "RefusedCount", "RepeatedCount"], properties.Keys.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Repeated_call_is_refused_before_dispatch_while_its_sibling_executes()
    {
        var executedCallIds = new ConcurrentBag<string>();
        for (var round = 1; round <= 4; round++)
        {
            _fakeChatClient.PlannedResponses.Enqueue([
                SearchCall($"repeat-{round}", "repeat"),
                SearchCall($"diagnostic-{round}", $"diagnostic-{round}")]);
        }
        _fakeToolExecutor.ExecuteOverride = (call, _) =>
        {
            executedCallIds.Add(call.CallId);
            return Task.FromResult($"result:{call.Arguments!["query"]}");
        };

        var sessionId = new SessionId("channel-cycle/refused-sibling");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("refused-sibling-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Repeat one search and run a new diagnostic."
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var callOutputs = new List<ToolCallOutput>();
        var resultOutputs = new List<ToolResultOutput>();
        // Round 3 is the first correction. Round 4 repeats the refused call next to a new diagnostic.
        for (var round = 0; round < 4; round++)
        {
            callOutputs.Add(await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken));
            callOutputs.Add(await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken));
            resultOutputs.Add(await subscriber.ExpectMsgAsync<ToolResultOutput>(cancellationToken: TestContext.Current.CancellationToken));
            resultOutputs.Add(await subscriber.ExpectMsgAsync<ToolResultOutput>(cancellationToken: TestContext.Current.CancellationToken));
        }
        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        var resultsByCallId = resultOutputs.ToDictionary(
            static result => result.CallId.Value,
            static result => result.Result,
            StringComparer.Ordinal);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Equal(
            ["diagnostic-1", "diagnostic-2", "diagnostic-3", "diagnostic-4", "repeat-1", "repeat-2"],
            executedCallIds.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("repeat-3", executedCallIds);
        Assert.DoesNotContain("repeat-4", executedCallIds);
        Assert.Equal(5, _fakeChatClient.CallCount);
        Assert.Equal(6, _fakeToolExecutor.CallCount);
        Assert.Equal(8, callOutputs.Count);
        Assert.Equal("result:diagnostic-3", resultsByCallId["diagnostic-3"]);
        Assert.Equal("result:diagnostic-4", resultsByCallId["diagnostic-4"]);
        Assert.Contains("repeated action-and-outcome cycle", resultsByCallId["repeat-3"], StringComparison.Ordinal);
        Assert.Contains("repeated action-and-outcome cycle", resultsByCallId["repeat-4"], StringComparison.Ordinal);
        Assert.DoesNotContain(_fakeChatClient.ReceivedMessages[3], IsRepeatNudge);
        Assert.True(IsRepeatNudge(_fakeChatClient.ReceivedMessages[4][^1]));
    }

    // The fifth refused repeat of a turn gets the escalated nudge. Only that text has this sentence.
    private const string EscalationMarker = "Stop trying this action.";
    private const string AnyRepeatNudgeStart = "[system: Netclaw refused your repeated call to ";

    private static bool IsAnyRepeatNudge(ChatMessage message)
        => message.Role == Microsoft.Extensions.AI.ChatRole.User
            && message.Text.StartsWith(AnyRepeatNudgeStart, StringComparison.Ordinal);

    private static bool IsEscalatedNudge(ChatMessage message)
        => IsRepeatNudge(message) && message.Text.Contains(EscalationMarker, StringComparison.Ordinal);

    private static bool IsPlainRepeatNudge(ChatMessage message)
        => IsRepeatNudge(message) && !message.Text.Contains(EscalationMarker, StringComparison.Ordinal);

    private async Task<(TestProbe Subscriber, IActorRef SessionManager)> JoinRepeatSessionAsync(SessionId sessionId)
    {
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        return (subscriber, sessionManager);
    }

    private static async Task<List<object>> SendAndCollectTurnAsync(
        IActorRef sessionManager, TestProbe subscriber, SessionId sessionId, string content,
        MessageSource? source = null)
    {
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = source
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var outputs = new List<object>();
        await subscriber.FishForMessageAsync<object>(message =>
        {
            outputs.Add(message);
            return message is TurnCompleted;
        }, TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);
        return outputs;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fifth_refused_repeat_escalates_and_the_sixth_ends_the_turn_quietly(bool finalResponseHasText)
    {
        // Aaron's rule (18 Oct 2026): the loop gets an end, and the person never sees a Netclaw error.
        // Rounds 1 and 2 run. Round 3 is the first correction. Rounds 4 to 7 are refused repeats 1 to 4.
        // Round 8 is repeat 5 and gets the escalated nudge. Round 9 is repeat 6, so the turn ends quietly.
        const string FinalText = "The browser search stays blocked by the repeat guard.";
        const string FollowUpAnswer = "The search did not change, so I stopped.";
        for (var round = 1; round <= 8; round++)
            _fakeChatClient.PlannedResponses.Enqueue([SearchCall($"cycle-call-{round}", "browser")]);
        _fakeChatClient.PlannedResponses.Enqueue(finalResponseHasText
            ? [new TextContent(FinalText), SearchCall("cycle-call-9", "browser")]
            : [SearchCall("cycle-call-9", "browser")]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent(FollowUpAnswer)]);
        _fakeToolExecutor.Results["search_tools"] = "same result";
        var sessionId = new SessionId($"channel-cycle/repeat-end-{finalResponseHasText}");
        var (subscriber, sessionManager) = await JoinRepeatSessionAsync(sessionId);

        var outputs = await SendAndCollectTurnAsync(sessionManager, subscriber, sessionId, "Repeat the same search.");

        // Repeats 1 to 4 get the plain nudge. Repeat 5 gets the escalated nudge. Only the latest nudge stays.
        var requests = _fakeChatClient.ReceivedMessages;
        Assert.DoesNotContain(requests[3], IsAnyRepeatNudge);
        for (var request = 4; request <= 7; request++)
            Assert.True(IsPlainRepeatNudge(requests[request][^1]), $"Request {request} must end with the plain nudge.");
        var escalation = requests[8][^1];
        Assert.True(IsEscalatedNudge(escalation), $"The last message was: {escalation.Text}");
        Assert.Contains("End your turn", escalation.Text, StringComparison.Ordinal);
        Assert.Contains("Tell the person what blocked you", escalation.Text, StringComparison.Ordinal);
        Assert.Contains("did not execute", escalation.Text, StringComparison.Ordinal);
        // The model must know that a sibling call next to a repeat does not run either.
        Assert.Contains("No call in that response runs.", escalation.Text, StringComparison.Ordinal);
        for (var request = 4; request <= 8; request++)
            Assert.Single(requests[request], IsAnyRepeatNudge);

        // Repeat 6 ends the turn: no further model request, no execution, and no tool line without a result.
        Assert.Equal(9, _fakeChatClient.CallCount);
        Assert.Equal(2, _fakeToolExecutor.CallCount);
        var results = outputs.OfType<ToolResultOutput>().ToList();
        Assert.Equal(8, results.Count);
        Assert.DoesNotContain(results, static result => result.CallId.Value == "cycle-call-9");
        Assert.DoesNotContain(outputs.OfType<ToolCallOutput>(), static call => call.CallId.Value == "cycle-call-9");

        // The person sees no error. The turn completes with only the text that the model wrote.
        Assert.DoesNotContain(outputs, static output => output is ErrorOutput);
        var completed = Assert.IsType<TurnCompleted>(outputs[^1]);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        // Only this end sets the flag. A channel binding then posts no empty-turn fallback, because that text is
        // Netclaw text, not text of the model.
        Assert.True(completed.QuietEnd);
        var texts = outputs.OfType<TextOutput>().Select(static output => output.Text).ToList();
        string[] expectedTexts = finalResponseHasText ? [FinalText] : [];
        Assert.Equal(expectedTexts, texts);

        // The session takes the next message. History holds no tool call without a result.
        var followUp = await SendAndCollectTurnAsync(sessionManager, subscriber, sessionId, "What happened?");
        Assert.DoesNotContain(followUp, static output => output is ErrorOutput);
        var followUpCompleted = Assert.IsType<TurnCompleted>(followUp[^1]);
        Assert.Equal(TurnOutcome.Completed, followUpCompleted.Outcome);
        Assert.False(followUpCompleted.QuietEnd);
        Assert.Equal([FollowUpAnswer], followUp.OfType<TextOutput>().Select(static output => output.Text));
        // ReceivedMessages is a snapshot, so read it again after the follow-up turn.
        var followUpRequest = _fakeChatClient.ReceivedMessages.Last(static request =>
            request.Any(static message => message.Text == "What happened?"));
        Assert.DoesNotContain(
            followUpRequest.SelectMany(static message => message.Contents.OfType<FunctionCallContent>()),
            static call => call.CallId == "cycle-call-9");
        Assert.Equal(finalResponseHasText, followUpRequest.Any(static message =>
            message.Role == Microsoft.Extensions.AI.ChatRole.Assistant && message.Text == FinalText));
    }

    [Fact]
    public async Task Only_the_latest_repeat_nudge_stays_in_history()
    {
        // Rounds 1 and 2 run, round 3 is the first correction, and rounds 4 to 6 are refused repeats 1 to 3.
        // Each repeat nudge replaces the one before it, so equal nudges do not pile up in the context.
        for (var round = 1; round <= 6; round++)
            _fakeChatClient.PlannedResponses.Enqueue([SearchCall($"cycle-call-{round}", "browser")]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("The search did not change.")]);
        _fakeToolExecutor.Results["search_tools"] = "same result";
        var sessionId = new SessionId("channel-cycle/one-nudge");
        var (subscriber, sessionManager) = await JoinRepeatSessionAsync(sessionId);

        var outputs = await SendAndCollectTurnAsync(sessionManager, subscriber, sessionId, "Repeat the same search.");

        Assert.Equal(TurnOutcome.Completed, Assert.IsType<TurnCompleted>(outputs[^1]).Outcome);
        var requests = _fakeChatClient.ReceivedMessages;
        Assert.Equal(7, requests.Count);
        for (var request = 4; request <= 6; request++)
        {
            Assert.True(IsPlainRepeatNudge(requests[request][^1]), $"Request {request} must end with the nudge.");
            Assert.Single(requests[request], IsAnyRepeatNudge);
        }
    }

    [Fact]
    public async Task Person_input_that_joins_the_turn_starts_a_new_refused_repeat_count()
    {
        // Decision: person input that joins the running turn clears the refusals, so it also starts a new count.
        // Without the reset, the first repeat after the join is repeat 5 and gets the escalated nudge.
        var sessionId = new SessionId("channel-cycle/repeat-count-join");
        var (subscriber, sessionManager) = await JoinRepeatSessionAsync(sessionId);
        // Window 1: two runs, the correction, and refused repeats 1 to 4. Then a new call runs, and the person writes.
        for (var round = 1; round <= 7; round++)
            _fakeChatClient.PlannedResponses.Enqueue([SearchCall($"first-{round}", "browser")]);
        _fakeChatClient.PlannedResponses.Enqueue([SearchCall("join", "join")]);
        // Window 2: two runs, the correction, refused repeats 1 to 5, and then a text answer.
        for (var round = 1; round <= 8; round++)
            _fakeChatClient.PlannedResponses.Enqueue([SearchCall($"second-{round}", "browser")]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("The search stays blocked.")]);
        _fakeToolExecutor.ExecuteOverride = async (call, ct) =>
        {
            if (call.CallId == "join")
            {
                // The person writes while the new call runs, so the message joins the running turn.
                await sessionManager.Ask<CommandAck>(new SendUserMessage
                {
                    SessionId = sessionId,
                    Content = "Keep trying, please."
                }, TimeSpan.FromSeconds(3), ct);
            }

            return "same result";
        };

        var outputs = await SendAndCollectTurnAsync(sessionManager, subscriber, sessionId, "Repeat the same search.");

        Assert.DoesNotContain(outputs, static output => output is ErrorOutput);
        Assert.Equal(TurnOutcome.Completed, Assert.IsType<TurnCompleted>(outputs[^1]).Outcome);
        Assert.Equal(["The search stays blocked."], outputs.OfType<TextOutput>().Select(static output => output.Text));
        Assert.Equal(17, _fakeChatClient.CallCount);
        var requests = _fakeChatClient.ReceivedMessages;
        Assert.Contains(requests[8], static message => message.Text == "Keep trying, please.");
        for (var request = 4; request <= 7; request++)
            Assert.True(IsPlainRepeatNudge(requests[request][^1]), $"Request {request} must end with the plain nudge.");
        // Window 2 counts from 1 again: repeats 1 to 4 are plain, and repeat 5 is the escalation.
        for (var request = 12; request <= 15; request++)
            Assert.True(IsPlainRepeatNudge(requests[request][^1]), $"Request {request} must end with the plain nudge.");
        Assert.True(IsEscalatedNudge(requests[16][^1]), $"The last message was: {requests[16][^1].Text}");
    }

    [Fact]
    public async Task System_input_that_joins_an_automation_turn_does_not_reset_the_refused_repeat_count()
    {
        // The requester of a reminder turn is the fixed system sender "reminder-system". Another reminder fire
        // into the same session has the same requester, so it joins the running turn. Only person input resets
        // the count (S1 of the re-review): an automation fire rate must not stretch the bound of the loop.
        // The join still clears the recurrence evidence, so the refused call runs two more times first.
        var sessionId = new SessionId("channel-cycle/repeat-count-system-join");
        var (subscriber, sessionManager) = await JoinRepeatSessionAsync(sessionId);
        // Window 1: two runs, the correction, and refused repeats 1 to 4. Then a new call runs, and the fire joins.
        for (var round = 1; round <= 7; round++)
            _fakeChatClient.PlannedResponses.Enqueue([SearchCall($"first-{round}", "browser")]);
        _fakeChatClient.PlannedResponses.Enqueue([SearchCall("join", "join")]);
        // Window 2: two runs, the correction, and refused repeat 5 (count 4 stays). Then a text answer.
        for (var round = 1; round <= 4; round++)
            _fakeChatClient.PlannedResponses.Enqueue([SearchCall($"second-{round}", "browser")]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("The search stays blocked.")]);
        _fakeToolExecutor.ExecuteOverride = async (call, ct) =>
        {
            if (call.CallId == "join")
            {
                await sessionManager.Ask<CommandAck>(new SendUserMessage
                {
                    SessionId = sessionId,
                    Content = "Check the search again.",
                    Source = ReminderSource("check-b:2000")
                }, TimeSpan.FromSeconds(3), ct);
            }

            return "same result";
        };

        var outputs = await SendAndCollectTurnAsync(
            sessionManager, subscriber, sessionId, "Check the search.", ReminderSource("check-a:1000"));

        Assert.DoesNotContain(outputs, static output => output is ErrorOutput);
        Assert.Equal(TurnOutcome.Completed, Assert.IsType<TurnCompleted>(outputs[^1]).Outcome);
        Assert.Equal(["The search stays blocked."], outputs.OfType<TextOutput>().Select(static output => output.Text));
        Assert.Equal(13, _fakeChatClient.CallCount);
        var requests = _fakeChatClient.ReceivedMessages;
        Assert.Contains(requests[8], static message => message.Text == "Check the search again.");
        for (var request = 4; request <= 7; request++)
            Assert.True(IsPlainRepeatNudge(requests[request][^1]), $"Request {request} must end with the plain nudge.");
        // The join cleared the evidence, so request 11 ends with the correction result and no nudge.
        Assert.False(IsAnyRepeatNudge(requests[11][^1]), $"The last message was: {requests[11][^1].Text}");
        // The count stayed at 4, so the first refused repeat after the join is repeat 5.
        Assert.True(IsEscalatedNudge(requests[12][^1]), $"The last message was: {requests[12][^1].Text}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fresh_turn_starts_a_new_refused_repeat_count(bool automationStartsTurn)
    {
        // The count belongs to a turn. A turn that ended with a count of 3 must not leave it for the next turn,
        // whoever starts that turn: a person, or an automation fire (a reminder, a webhook, a job result).
        // Otherwise an automation session that no person ever writes to would reach the quiet end at its first
        // refused repeat.
        var sessionId = new SessionId($"channel-cycle/repeat-count-fresh-{automationStartsTurn}");
        var (subscriber, sessionManager) = await JoinRepeatSessionAsync(sessionId);
        // Turn 1: two runs, the correction, refused repeats 1 to 3, and a text answer.
        for (var round = 1; round <= 6; round++)
            _fakeChatClient.PlannedResponses.Enqueue([SearchCall($"first-{round}", "browser")]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("I stop here.")]);
        // Turn 2: two runs, the correction, refused repeats 1 to 5, and a text answer.
        for (var round = 1; round <= 8; round++)
            _fakeChatClient.PlannedResponses.Enqueue([SearchCall($"second-{round}", "browser")]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("The search stays blocked.")]);
        _fakeToolExecutor.Results["search_tools"] = "same result";

        await SendAndCollectTurnAsync(sessionManager, subscriber, sessionId, "Repeat the same search.");
        var outputs = await SendAndCollectTurnAsync(
            sessionManager, subscriber, sessionId, "Check the search again.",
            automationStartsTurn ? ReminderSource("check-a:1000") : null);

        Assert.DoesNotContain(outputs, static output => output is ErrorOutput);
        Assert.Equal(["The search stays blocked."], outputs.OfType<TextOutput>().Select(static output => output.Text));
        Assert.Equal(16, _fakeChatClient.CallCount);
        var requests = _fakeChatClient.ReceivedMessages;
        for (var request = 11; request <= 14; request++)
            Assert.True(IsPlainRepeatNudge(requests[request][^1]), $"Request {request} must end with the plain nudge.");
        Assert.True(IsEscalatedNudge(requests[15][^1]), $"The last message was: {requests[15][^1].Text}");
    }

    [Fact]
    public async Task Parallel_cycle_correction_preserves_every_call_result_pair()
    {
        // Arrange
        static List<AIContent> ParallelCalls(int round) =>
        [
            SearchCall($"cycle-{round}-a", "alpha"),
            SearchCall($"cycle-{round}-b", "beta"),
            SearchCall($"cycle-{round}-c", "gamma")
        ];
        for (var round = 1; round <= 4; round++)
            _fakeChatClient.PlannedResponses.Enqueue(ParallelCalls(round));
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("The parallel searches repeat.")]);
        _fakeToolExecutor.Results["search_tools"] = "same result";

        var sessionId = new SessionId("channel-cycle/parallel-pairs");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("parallel-cycle-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Repeat the parallel search."
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var correctionResults = new List<ToolResultOutput>();
        for (var batch = 0; batch < 4; batch++)
        {
            for (var call = 0; call < 3; call++)
                await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken);
            for (var call = 0; call < 3; call++)
            {
                var toolResult = await subscriber.ExpectMsgAsync<ToolResultOutput>(
                    cancellationToken: TestContext.Current.CancellationToken);
                if (batch >= 2)
                    correctionResults.Add(toolResult);
            }
        }

        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Equal(
            ["cycle-3-a", "cycle-3-b", "cycle-3-c", "cycle-4-a", "cycle-4-b", "cycle-4-c"],
            correctionResults.Select(static result => result.CallId.Value)
                .OrderBy(static callId => callId, StringComparer.Ordinal));
        Assert.All(correctionResults, static result =>
        {
            Assert.Contains("repeated action-and-outcome cycle", result.Result, StringComparison.Ordinal);
            Assert.Contains("This call did not execute.", result.Result, StringComparison.Ordinal);
        });
        Assert.Equal(6, _fakeToolExecutor.CallCount);
        Assert.Equal(5, _fakeChatClient.CallCount);
        var feedback = _fakeChatClient.ReceivedMessages[4];
        var calls = feedback.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
        var results = feedback.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
        Assert.Equal(12, calls.Length);
        Assert.Equal(12, results.Length);
        foreach (var call in calls)
        {
            var result = Assert.Single(results, candidate => candidate.CallId == call.CallId);
            var correction = correctionResults.SingleOrDefault(candidate => candidate.CallId.Value == call.CallId);
            Assert.Equal(correction?.Result ?? "same result", result.Result?.ToString());
        }

        // One nudge follows the last result of the repeated batch.
        Assert.True(IsRepeatNudge(feedback[^1]));
        Assert.Single(feedback, IsRepeatNudge);
    }

    [Fact]
    public async Task Native_correction_exposes_deferred_tool_on_next_request_but_not_after_recovery()
    {
        const string deferredToolName = "deferred_native_probe";
        _toolRegistry.Register(new DeferredTestTool(deferredToolName));
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-native-correction",
                "shell_execute",
                new Dictionary<string, object?> { ["command"] = $"{deferredToolName} --inspect" })
        ];
        _fakeToolExecutor.Corrections["shell_execute"] =
            new ToolCorrection.NativeToolSuggested(new ToolName(deferredToolName));

        var sessionId = new SessionId("channel-discovery/native-correction-recovery");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("native-correction-recovery-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Inspect with the native tool."
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        var correction = await subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(6),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(6),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(deferredToolName, _fakeChatClient.ReceivedToolNames[0]);
        Assert.Contains(deferredToolName, _fakeChatClient.ReceivedToolNames[1]);
        Assert.Contains(deferredToolName, correction.Result, StringComparison.Ordinal);
        var recordedCorrection = _fakeChatClient.ReceivedMessages[1]
            .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Single(result => result.CallId == "call-native-correction");
        Assert.Equal(correction.Result, recordedCorrection.Result?.ToString());

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);

        var recoveredSubscriber = CreateTestProbe("native-correction-recovered-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(recoveredSubscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await recoveredSubscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Continue after recovery."
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await recoveredSubscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(6),
            cancellationToken: TestContext.Current.CancellationToken);
        await recoveredSubscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(6),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(deferredToolName, _fakeChatClient.ReceivedToolNames[^1]);
    }

    [Fact]
    public async Task Context_overflow_evicts_a_loaded_deferred_tool_before_retry()
    {
        const string deferredToolName = "browser_chrome_devtools__navigate_page";
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-load-overflow", "load_tool",
                new Dictionary<string, object?> { ["Name"] = "browser_chrome_devtools/navigate_page" })
        ];
        _fakeToolExecutor.Results["load_tool"] = "browser_chrome_devtools/navigate_page";

        var sessionId = new SessionId("channel-discovery/overflow-eviction");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("overflow-eviction-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Load the browser tool."
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(deferredToolName, _fakeChatClient.ReceivedToolNames[^1]);

        _fakeChatClient.PlannedExceptions.Enqueue(new ProviderException(
            "maximum context length exceeded",
            "HTTP 400: maximum context length exceeded",
            statusCode: 400));

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Continue after the overflow."
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ErrorOutput>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<CompactionOutput>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(deferredToolName, _fakeChatClient.ReceivedToolNames[^1]);
    }

    [Fact]
    public async Task Native_correction_for_core_tool_does_not_duplicate_schema()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-core-correction",
                "shell_execute",
                new Dictionary<string, object?> { ["command"] = "load_tool --help" })
        ];
        _fakeToolExecutor.Corrections["shell_execute"] =
            new ToolCorrection.NativeToolSuggested(new ToolName("load_tool"));

        var sessionId = new SessionId("channel-discovery/native-core-noop");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("native-core-noop-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Inspect the loader."
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(6),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(6),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _fakeChatClient.ReceivedToolNames.Count);
        Assert.Equal(_fakeChatClient.ReceivedToolNames[0], _fakeChatClient.ReceivedToolNames[1]);
        Assert.Single(_fakeChatClient.ReceivedToolNames[1], static name => name == "load_tool");
    }

    [Fact]
    public async Task Native_correction_does_not_expose_tool_denied_before_activation()
    {
        const string deferredToolName = "deferred_native_denied_before_activation";
        _toolRegistry.Register(new DeferredTestTool(deferredToolName));
        _fakeToolExecutor.BeforeCorrection = () =>
        {
            foreach (var profile in _toolConfig.AudienceProfiles.GetAllProfiles())
            {
                profile.ApprovalPolicy ??= new ToolApprovalConfig();
                profile.ApprovalPolicy.ToolOverrides[deferredToolName] = ToolApprovalMode.Deny;
            }
        };

        var nextRequestTools = await RunNativeCorrectionTurnAsync(
            "native-correction-policy-change",
            deferredToolName);

        Assert.DoesNotContain(deferredToolName, nextRequestTools);
    }

    [Fact]
    public async Task Native_correction_does_not_expose_missing_registration()
    {
        const string missingToolName = "missing_native_registration";

        var nextRequestTools = await RunNativeCorrectionTurnAsync(
            "native-correction-missing-registration",
            missingToolName);

        Assert.DoesNotContain(missingToolName, nextRequestTools);
    }

    private async Task<IReadOnlyList<string>> RunNativeCorrectionTurnAsync(
        string sessionSuffix,
        string toolName)
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-" + sessionSuffix,
                "shell_execute",
                new Dictionary<string, object?> { ["command"] = toolName })
        ];
        _fakeToolExecutor.Corrections["shell_execute"] =
            new ToolCorrection.NativeToolSuggested(new ToolName(toolName));

        var sessionId = new SessionId("channel-discovery/" + sessionSuffix);
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe(sessionSuffix + "-sub");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use the named native tool."
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(6),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(6),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _fakeChatClient.ReceivedToolNames.Count);
        return _fakeChatClient.ReceivedToolNames[1];
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
    public async Task Session_passivates_with_an_active_subscriber_when_idle()
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

        // A live subscriber does not block idle passivation.
        child.Tell(ReceiveTimeout.Instance);
        await ExpectTerminatedAsync(
            child,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);
    }

    [Fact]
    public async Task Processing_turn_ignores_idle_timeout_then_passivates_after_return_to_ready()
    {
        var sessionId = new SessionId("test-channel/deactivate-on-passivate");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("deactivate-passivate-sub");
        var busy = CreateTestProbe("processing-busy-state");
        await sessionManager.Ask<SessionJoined>(new JoinSession(busy)
        {
            SessionId = sessionId,
            Filter = OutputFilter.ProcessingState
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await busy.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
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
            Content = "Keep processing while idle timeout arrives"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await _fakeChatClient.FirstCallEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);

        Assert.True((await busy.ExpectMsgAsync<ProcessingStateOutput>(
            cancellationToken: TestContext.Current.CancellationToken)).IsProcessing);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        child.Tell(ReceiveTimeout.Instance);
        var processingJoin = await child.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, processingJoin.SessionId);
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(sessionId.Value, _lifecycleObserver.DeactivatedSessionIds);

        child.Tell(new JoinSession(busy)
        {
            SessionId = sessionId,
            Filter = OutputFilter.ProcessingState
        }, busy);
        await busy.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        responseGate.TrySetResult();
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await busy.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False((await busy.ExpectMsgAsync<ProcessingStateOutput>(
            cancellationToken: TestContext.Current.CancellationToken)).IsProcessing);

        child.Tell(ReceiveTimeout.Instance);
        await busy.ExpectMsgAsync<SessionDeactivated>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync(
            m => m is SessionDeactivated,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
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
            Filter = OutputFilter.TextOnly | OutputFilter.ProcessingState
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}").ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        var ack = await sessionManager.Ask<CommandAck>(new PrepareForDaemonRestart(sessionId, "config-reload"), TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(sessionId, ack.SessionId);
        await subscriber.ExpectMsgAsync<SessionDeactivated>(cancellationToken: TestContext.Current.CancellationToken);
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

        var drainTask = sessionManager.Ask<DaemonRestartPrepared>(new PrepareForDaemonRestart(sessionId, "config-reload"), TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

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
        var drainAck = await drainTask;
        Assert.Equal(sessionId, drainAck.SessionId);
        Assert.Null(drainAck.RestartReminder);
        await ExpectTerminatedAsync(child, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(_fakeChatClient.ReceivedMessages, conversation =>
            conversation.Any(msg =>
                msg.Text is not null
                && msg.Text.Contains("msg_too_long", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Restart_reminder_restores_an_interrupted_input_without_a_second_user_prompt()
    {
        var sessionId = new SessionId("signalr/restart-resume");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeChatClient.NextResponseGate = responseGate;
        var source = new MessageSource
        {
            ChannelType = ChannelType.SignalR,
            SenderId = new SenderId("operator-1"),
            MessageId = "restart-input-1",
            TurnId = new Netclaw.Actors.Protocol.TurnId("restart-turn-1"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
            ReceivedAt = _timeProvider.GetUtcNow()
        };

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Finish the interrupted task",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await _fakeChatClient.FirstCallEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var child = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);

        var drainAck = await sessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "daemon-stop"),
            TimeSpan.FromSeconds(8),
            TestContext.Current.CancellationToken);
        var reminder = Assert.IsType<ReminderDefinition>(drainAck.RestartReminder);
        Assert.Equal(DeliveryKind.CurrentSession, reminder.Delivery.Kind);
        Assert.Equal(TrustAudience.Personal, reminder.Audience);
        Assert.Equal(TrustBoundary.Personal, reminder.Boundary);
        Assert.Equal(TimeSpan.FromMinutes(10), reminder.ExpiresAt - reminder.CreatedAt);
        await ExpectTerminatedAsync(
            child,
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        var subscriber = CreateTestProbe("restart-resume-subscriber");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = reminder.Instructions,
            Source = new MessageSource
            {
                ChannelType = ChannelType.SignalR,
                SenderId = new SenderId("reminder-system"),
                MessageId = $"{reminder.Id}:1",
                TurnId = new Netclaw.Actors.Protocol.TurnId($"{reminder.Id}:1"),
                Audience = reminder.Audience,
                Boundary = reminder.Boundary,
                Principal = PrincipalClassification.VerifiedAutomation,
                Provenance = new SourceProvenance(
                    TransportAuthenticity.LocalProcess,
                    PayloadTaint.Trusted),
                ReceivedAt = _timeProvider.GetUtcNow(),
                ReminderId = new ReminderId($"{reminder.Id}:1")
            }
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        var resumedCall = _fakeChatClient.ReceivedMessages.Last(conversation =>
            conversation.Any(message => string.Equals(
                message.Text,
                "Finish the interrupted task",
                StringComparison.Ordinal)));
        Assert.DoesNotContain(resumedCall, message => string.Equals(
            message.Text,
            reminder.Instructions,
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_drain_does_not_remind_after_partial_text()
    {
        var sessionId = new SessionId("signalr/restart-partial-text");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("restart-partial-text-subscriber");
        await JoinSessionAsync(sessionManager, subscriber, sessionId, OutputFilter.TextStreaming);
        _fakeChatClient.StreamCompletionGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Do not repeat a partial reply",
            Source = new MessageSource
            {
                ChannelType = ChannelType.SignalR,
                SenderId = new SenderId("operator-1"),
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                Principal = PrincipalClassification.Operator,
                Provenance = new SourceProvenance(
                    TransportAuthenticity.Verified,
                    PayloadTaint.Trusted),
                ReceivedAt = _timeProvider.GetUtcNow()
            }
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextDeltaOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        var ack = await sessionManager.Ask<DaemonRestartPrepared>(
            new PrepareForDaemonRestart(sessionId, "daemon-stop"),
            TimeSpan.FromSeconds(8),
            TestContext.Current.CancellationToken);

        Assert.Null(ack.RestartReminder);
    }

    [Fact]
    public async Task Compacting_session_rejects_new_work_and_passivates_after_compaction_finishes()
    {
        var sessionId = new SessionId("test-channel/restart-drain-compacting");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("restart-drain-compacting-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var busy = CreateTestProbe("compaction-busy-state");
        await sessionManager.Ask<SessionJoined>(new JoinSession(busy)
        {
            SessionId = sessionId,
            Filter = OutputFilter.ProcessingState
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await busy.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        for (var turn = 1; turn <= 3; turn++)
        {
            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = $"Build compaction history {turn}"
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

            await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True((await busy.ExpectMsgAsync<ProcessingStateOutput>(
                cancellationToken: TestContext.Current.CancellationToken)).IsProcessing);
            await busy.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.False((await busy.ExpectMsgAsync<ProcessingStateOutput>(
                cancellationToken: TestContext.Current.CancellationToken)).IsProcessing);
        }

        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 200_000,
            OutputTokenCount = 1,
            TotalTokenCount = 200_001
        };
        var observationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeChatClient.ObservationResponseGate = observationGate;

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Trigger compaction"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
        {
            Assert.Contains(_fakeChatClient.ReceivedMessages, conversation =>
                conversation.Any(message =>
                    message.Role == Microsoft.Extensions.AI.ChatRole.System
                    && message.Text?.Contains("You are a session summarizer", StringComparison.Ordinal) == true));
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(50), cancellationToken: TestContext.Current.CancellationToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}").ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);
        Assert.True((await busy.ExpectMsgAsync<ProcessingStateOutput>(
            cancellationToken: TestContext.Current.CancellationToken)).IsProcessing);
        await busy.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        child.Tell(new JoinSession(busy)
        {
            SessionId = sessionId,
            Filter = OutputFilter.ProcessingState
        }, busy);
        await busy.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        sessionManager.Tell(new PrepareForDaemonRestart(sessionId, "config-reload"), TestActor);
        sessionManager.Tell(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Should be rejected during compaction"
        }, TestActor);

        try
        {
            var nack = await ExpectMsgAsync<CommandNack>(
                TimeSpan.FromSeconds(3),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SessionIngressGate.RestartInProgressMessage, nack.Reason);
        }
        finally
        {
            observationGate.TrySetResult();
        }

        var drainAck = await ExpectMsgAsync<DaemonRestartPrepared>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, drainAck.SessionId);
        await busy.ExpectMsgAsync<CompactionOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False((await busy.ExpectMsgAsync<ProcessingStateOutput>(
            cancellationToken: TestContext.Current.CancellationToken)).IsProcessing);
        await busy.ExpectMsgAsync<SessionDeactivated>(cancellationToken: TestContext.Current.CancellationToken);
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
    public async Task Failed_turn_discards_stale_working_context_continuation()
    {
        _workingContextSnapshots.Pending = new TaskCompletionSource<WorkingContextSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeChatClient.PlannedExceptions.Enqueue(new IOException("fail after context inspection"));
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
        child.Tell(new WorkingContextSnapshotReady(
            Generation: 1,
            Snapshot: new WorkingContextSnapshot
            {
                WorkingContext = WorkingContext.Empty.WithProjectDirectory("/current/project"),
                Git = new GitWorkingContextInspection.NotRepository()
            }), TestActor);

        await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        child.Tell(new WorkingContextSnapshotReady(
            Generation: 1,
            Snapshot: new WorkingContextSnapshot
            {
                WorkingContext = WorkingContext.Empty.WithProjectDirectory("/stale/project"),
                Git = new GitWorkingContextInspection.NotRepository()
            }), TestActor);

        _workingContextSnapshots.Pending = null;
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Start a fresh turn after the failed turn"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        child.Tell(new JoinSession(TestActor)
        {
            SessionId = sessionId,
            Filter = OutputFilter.None
        }, TestActor);
        await ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(_fakeChatClient.ReceivedMessages);
        var prompt = string.Join('\n', _fakeChatClient.ReceivedMessages.Single().Select(message => message.Text));
        Assert.DoesNotContain("/stale/project", prompt, StringComparison.Ordinal);
        Assert.Contains("Start a fresh turn", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_tool_callbacks_do_not_change_a_new_dispatch()
    {
        var oldTokenReady = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentTokenReady = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCurrent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeToolExecutor.ExecuteOverride = async (call, token) =>
        {
            var query = (string)call.Arguments!["query"]!;
            if (query == "old")
            {
                oldTokenReady.TrySetResult(token);
                return "old result";
            }

            currentTokenReady.TrySetResult(token);
            return await releaseCurrent.Task.WaitAsync(token);
        };
        _fakeChatClient.PlannedResponses.Enqueue([
            new FunctionCallContent("old-call", "web_search",
                new Dictionary<string, object?> { ["query"] = "old" })]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("First turn done.")]);
        _fakeChatClient.PlannedResponses.Enqueue([
            new FunctionCallContent("current-call", "web_search",
                new Dictionary<string, object?> { ["query"] = "current" })]);
        _fakeChatClient.PlannedResponses.Enqueue([new TextContent("Second turn done.")]);

        var sessionId = new SessionId("test-channel/stale-tool-dispatch");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("stale-tool-dispatch-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId, OutputFilter.Full);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Complete the first tool call"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        var oldToken = await oldTokenReady.Task.WaitAsync(TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Complete a new tool call"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(cancellationToken: TestContext.Current.CancellationToken);
        var currentToken = await currentTokenReady.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(oldToken, currentToken);

        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        child.Tell(new ToolExecutionSingleCompleted(
            new ToolCallResult(
                new SerializableChatMessage { Role = Netclaw.Actors.Protocol.ChatRole.Tool, Content = "stale result" },
                [], [], [], default),
            oldToken), TestActor);
        child.Tell(new ToolExecutionFailed
        {
            Cause = new IOException("stale tool failure"),
            DispatchToken = oldToken
        }, TestActor);
        child.Tell(new ToolExecutionBatchCompleted(oldToken), TestActor);
        child.Tell(new JoinSession(TestActor)
        {
            SessionId = sessionId,
            Filter = OutputFilter.None
        }, TestActor);
        await ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        releaseCurrent.TrySetResult("current result");
        var currentResult = await subscriber.ExpectMsgAsync<ToolResultOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("current result", currentResult.Result);
        var final = await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Second turn done.", final.Text);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _fakeToolExecutor.CallCount);
        Assert.Equal(4, _fakeChatClient.CallCount);
        Assert.DoesNotContain(_fakeChatClient.ReceivedMessages.SelectMany(static messages => messages),
            static message => message.Contents.Any(content => content is TextContent { Text: "stale result" }));
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

internal sealed class DeferredTestTool(string name) : INetclawTool
{
    private readonly AIFunction _function = AIFunctionFactory.Create(() => "ok", name, "Deferred test tool");

    public string Name { get; } = name;
    public LlmFacingToolName LlmFacingName { get; } = LlmFacingToolName.FromCanonical(name);
    public string Description => "Deferred test tool";
    public string GrantCategory => "builtin";
    public JsonElement ParameterSchema => default;
    public AITool ToAITool() => _function;

    public Task<string> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        CancellationToken ct = default) => Task.FromResult("ok");
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
    /// When populated, finish reasons are dequeued per main-model response and set
    /// on <see cref="ChatResponse.FinishReason"/>. A null entry leaves it unset.
    /// Observation compressor and sidecar calls do not consume this queue.
    /// </summary>
    public Queue<ChatFinishReason?> PlannedFinishReasons { get; } = new();

    /// <summary>
    /// Holds a compaction observation call until the test releases its response.
    /// </summary>
    public TaskCompletionSource? ObservationResponseGate { get; set; }

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
    /// The finish reason of each planned response when <see cref="PlannedFinishReasons"/> is empty.
    /// Null models a stream that ended with no finish reason. A real provider that completes a reply
    /// sends one.
    /// </summary>
    public ChatFinishReason? PlannedFinishReason { get; set; }

    /// <summary>This fake selects response content from the captured request.</summary>
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AIContent>?>? ResponseContentsSelector { get; set; }

    public Action<int>? AfterToolCallResponse { get; set; }

    /// <summary>
    /// When populated, exceptions are dequeued and thrown before processing the
    /// response. Used to simulate provider errors (502, 400 context overflow, etc.).
    /// Exceptions are thrown before incrementing <see cref="CallCount"/>.
    /// </summary>
    public Queue<Exception> PlannedExceptions { get; } = new();

    public TaskCompletionSource? NextResponseGate { get; set; }

    public TaskCompletionSource? StreamCompletionGate { get; set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialize the caller's own enumerables outside the lock — they are private to this
        // call and can be non-trivial to enumerate; only the shared-list appends need guarding.
        var messageList = messages.ToList();
        var toolNames = options?.Tools?
            .Select(t => t is AIFunctionDeclaration f ? f.Name : t.GetType().Name)
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

            if (ObservationResponseGate is { } observationGate)
                await observationGate.Task.WaitAsync(cancellationToken);

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
        var hasQueuedFinishReason = PlannedFinishReasons.TryDequeue(out var plannedFinishReason);

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
                var toolResponse = new ChatResponse(toolCallMessage) { FinishReason = plannedFinishReason };
                if (usageOverride is not null)
                    toolResponse.Usage = usageOverride;
                AfterToolCallResponse?.Invoke(_callCount);
                return toolResponse;
            }
        }

        if (ResponseContentsSelector?.Invoke(messageList) is { } selectedContents)
        {
            var selectedResponse = new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                new List<AIContent>(selectedContents)));
            if (usageOverride is not null)
                selectedResponse.Usage = usageOverride;
            return selectedResponse;
        }

        if (PlannedResponses.Count > 0)
        {
            var plannedContents = PlannedResponses.Dequeue();
            var plannedResponse = new ChatResponse(new ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                new List<AIContent>(plannedContents)))
            {
                FinishReason = hasQueuedFinishReason ? plannedFinishReason : PlannedFinishReason
            };
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

        var response = new ChatResponse(responseMessage) { FinishReason = plannedFinishReason };

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
        if (StreamCompletionGate is { } streamGate)
        {
            yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "partial ");
            yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "reply");
            using var registration = cancellationToken.Register(() => streamGate.TrySetCanceled(cancellationToken));
            await streamGate.Task;
            yield break;
        }

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
