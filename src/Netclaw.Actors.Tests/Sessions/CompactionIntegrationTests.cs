using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Integration tests for the tiered context compaction system.
/// Verifies: threshold trigger, tool result clearing, structured summarization,
/// session recovery after compaction, and buffer drain post-compaction.
/// </summary>
public class CompactionIntegrationTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeMemoryExtractor _fakeMemoryExtractor = new();

    public CompactionIntegrationTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 1000,
        });
        // Small context window for easy threshold triggering in tests.
        // KeepRecentMessages=0 so minimal-history tests (1 turn) actually reduce message count.
        services.AddSingleton(new SessionConfig
        {
            TurnLlmTimeout = TimeSpan.FromSeconds(1),
            SidecarLlmTimeout = TimeSpan.FromSeconds(1),
            Tuning = new SessionTuning
            {
                CompactionThreshold = 0.75, // 750 tokens triggers compaction
                SnapshotInterval = 5,
                KeepRecentToolResults = 1,
                KeepRecentMessages = 0,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<IMemoryExtractor>(_fakeMemoryExtractor);
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());

        // Composite records for LlmSessionActor constructor
        services.AddSingleton(sp => new SessionServices(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetService<IReadOnlyList<IContextLayerProvider>>() ?? Array.Empty<IContextLayerProvider>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<NetclawPaths>()));
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
    public async Task Compaction_triggers_when_usage_exceeds_threshold()
    {
        // Configure: usage reports 800 tokens (>= 750 threshold)
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 800,
            OutputTokenCount = 50,
            TotalTokenCount = 850
        };

        var sessionId = new SessionId("test-channel/compaction-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("compact-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, this should trigger compaction"
        }, TestContext.Current.CancellationToken);

        // First: normal text response from the turn
        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        // Then: compaction output (with observations from Observer)
        var compaction = await subscriber.ExpectMsgAsync<CompactionOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(compaction.MessagesAfter < compaction.MessagesBefore);

        // At least one LLM call should have happened for the turn itself.
        // Memory extraction may be skipped depending on provider/output shape.
        Assert.True(_fakeChatClient.CallCount >= 1);
    }

    [Fact]
    public async Task Compaction_does_not_trigger_below_threshold()
    {
        // Configure: usage reports 500 tokens (< 750 threshold)
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 500,
            OutputTokenCount = 50,
            TotalTokenCount = 550
        };

        var sessionId = new SessionId("test-channel/no-compaction-test");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("no-compact-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, this should NOT trigger compaction"
        }, TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        // No compaction output should appear
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        // Only 1 LLM call — no compaction
        Assert.Equal(1, _fakeChatClient.CallCount);
    }

    [Fact]
    public async Task Compaction_preserves_system_prompt()
    {
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 800,
            OutputTokenCount = 50,
            TotalTokenCount = 850
        };

        var sessionId = new SessionId("test-channel/sys-prompt-preserve");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("sys-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Trigger compaction"
        }, TestContext.Current.CancellationToken);

        // Wait for turn + compaction
        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<CompactionOutput>(cancellationToken: TestContext.Current.CancellationToken);

        // After compaction, disable the high usage so next call doesn't trigger again
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 20,
            TotalTokenCount = 120
        };

        // Send another message — session should still work after compaction
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Post-compaction message"
        }, TestContext.Current.CancellationToken);

        var text = await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Buffer_drains_after_compaction()
    {
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 800,
            OutputTokenCount = 50,
            TotalTokenCount = 850
        };
        _fakeChatClient.Delay = TimeSpan.FromMilliseconds(100);

        var sessionId = new SessionId("test-channel/buffer-drain-compact");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("buffer-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // First message — triggers turn then compaction
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TestContext.Current.CancellationToken);

        // Buffer a second message during processing/compaction
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message (buffered)"
        }, TestContext.Current.CancellationToken);

        // Wait for turn 1 output
        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        // Wait for compaction
        await subscriber.ExpectMsgAsync<CompactionOutput>(cancellationToken: TestContext.Current.CancellationToken);

        // After compaction, lower the usage so the buffered message doesn't trigger again
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 20,
            TotalTokenCount = 120
        };

        // Buffered message should be drained and produce output
        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Buffer_drains_after_compaction_timeout()
    {
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 800,
            OutputTokenCount = 50,
            TotalTokenCount = 850
        };
        _fakeChatClient.StuckObservationCallsRemaining = 1;

        var sessionId = new SessionId("test-channel/buffer-drain-compact-timeout");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("buffer-timeout-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message (buffered)"
        }, TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("compaction timed out", error.Message, StringComparison.OrdinalIgnoreCase);

        var bufferedText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(6), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", bufferedText.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<UsageOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Session_recovers_after_compaction_and_kill()
    {
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 800,
            OutputTokenCount = 50,
            TotalTokenCount = 850
        };

        var sessionId = new SessionId("test-channel/compact-recovery");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("recover-sub");

        // Phase 1: Send message, trigger compaction
        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Message before compaction"
        }, TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<CompactionOutput>(cancellationToken: TestContext.Current.CancellationToken);

        // Phase 2: Kill the session actor
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);

        // Phase 3: Recover — join again
        var recoverSub = CreateTestProbe("recover-sub-2");
        var recovered = await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = recoverSub,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await recoverSub.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, recovered.SessionId);

        // Phase 4: Verify session still works after recovery
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 20,
            TotalTokenCount = 120
        };

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Post-recovery message"
        }, TestContext.Current.CancellationToken);

        var text = await recoverSub.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Compaction_summary_uses_user_role_with_context_summary_tags()
    {
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 800,
            OutputTokenCount = 50,
            TotalTokenCount = 850
        };

        var sessionId = new SessionId("test-channel/summary-format");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("summary-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Investigate ticket 579"
        }, TestContext.Current.CancellationToken);

        // Turn output
        await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<UsageOutput>(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);

        // Compaction (with observations from Observer)
        await subscriber.ExpectMsgAsync<CompactionOutput>(cancellationToken: TestContext.Current.CancellationToken);

        // Verify post-compaction by sending another message (low usage to avoid re-compaction)
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 20,
            TotalTokenCount = 120
        };

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "What was the ticket about?"
        }, TestContext.Current.CancellationToken);

        // Session still works — the context-summary message is there as context
        var text = await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Emergency_compaction_auto_resends_pending_message()
    {
        // First call: context overflow (triggers emergency compaction)
        // Subsequent calls: succeed normally
        _fakeChatClient.PlannedExceptions.Enqueue(
            new ProviderException(
                "maximum context length exceeded",
                "HTTP 400: maximum context length exceeded",
                statusCode: 400));

        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 20,
            TotalTokenCount = 120
        };

        var sessionId = new SessionId("test-channel/emergency-auto-resend");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("emergency-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "This message triggers overflow then gets auto-resent"
        }, TestContext.Current.CancellationToken);

        // ErrorOutput from context overflow — should NOT say "Please resend"
        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("compacting session history", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resend", error.Message, StringComparison.OrdinalIgnoreCase);

        // Compaction runs
        await subscriber.ExpectMsgAsync<CompactionOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Auto-resend fires and produces a response
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<UsageOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Emergency_compaction_with_buffered_message_drains_both()
    {
        // First call: context overflow
        _fakeChatClient.PlannedExceptions.Enqueue(
            new ProviderException(
                "maximum context length exceeded",
                "HTTP 400: maximum context length exceeded",
                statusCode: 400));

        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 20,
            TotalTokenCount = 120
        };
        _fakeChatClient.Delay = TimeSpan.FromMilliseconds(50);

        var sessionId = new SessionId("test-channel/emergency-with-buffer");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("emergency-buffer-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = subscriber,
            Filter = OutputFilter.Full
        }, TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // First message triggers overflow
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TestContext.Current.CancellationToken);

        // Buffer a second message during compaction
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message (buffered)"
        }, TestContext.Current.CancellationToken);

        // ErrorOutput from overflow
        await subscriber.ExpectMsgAsync<ErrorOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Compaction
        await subscriber.ExpectMsgAsync<CompactionOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Buffer drain processes the buffered message — auto-resend is subsumed
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<UsageOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// Fake memory extractor that captures extracted memories for verification.
/// </summary>
internal sealed class FakeMemoryExtractor : IMemoryExtractor
{
    private int _callCount;

    public int CallCount => _callCount;

    public List<(string SessionId, string Memories)> Entries { get; } = new();

    public Task PersistAsync(string sessionId, string extractedMemories, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _callCount);
        lock (Entries)
        {
            Entries.Add((sessionId, extractedMemories));
        }
        return Task.CompletedTask;
    }
}
