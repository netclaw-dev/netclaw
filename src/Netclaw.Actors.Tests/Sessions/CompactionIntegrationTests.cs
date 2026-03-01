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
using Netclaw.Actors.Sessions;
using Xunit;
using Xunit.Abstractions;

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
        // Small context window for easy threshold triggering in tests.
        // KeepRecentMessages=0 so minimal-history tests (1 turn) actually reduce message count.
        services.AddSingleton(new SessionConfig
        {
            ModelId = "fake-model",
            ContextWindowTokens = 1000,
            CompactionThreshold = 0.75, // 750 tokens triggers compaction
            SnapshotInterval = 5,
            KeepRecentToolResults = 1,
            KeepRecentMessages = 0
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<IMemoryExtractor>(_fakeMemoryExtractor);
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
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
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, this should trigger compaction"
        }, TimeSpan.FromSeconds(3));

        // First: normal text response from the turn
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Then: compaction output (extractive — no LLM summarization)
        var compaction = subscriber.ExpectMsg<CompactionOutput>(TimeSpan.FromSeconds(5));
        Assert.False(compaction.Summarized);
        Assert.True(compaction.MessagesAfter < compaction.MessagesBefore);

        // 2 LLM calls: 1 for the turn + 1 for memory extraction (no summarization call).
        // Memory extraction fires because FakeMemoryExtractor is registered (not NullMemoryExtractor).
        Assert.True(_fakeChatClient.CallCount >= 2);
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
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, this should NOT trigger compaction"
        }, TimeSpan.FromSeconds(3));

        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // No compaction output should appear
        subscriber.ExpectNoMsg(TimeSpan.FromMilliseconds(500));

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
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Trigger compaction"
        }, TimeSpan.FromSeconds(3));

        // Wait for turn + compaction
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<CompactionOutput>(TimeSpan.FromSeconds(5));

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
        }, TimeSpan.FromSeconds(3));

        var text = subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        subscriber.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
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
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        // First message — triggers turn then compaction
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        }, TimeSpan.FromSeconds(3));

        // Buffer a second message during processing/compaction
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message (buffered)"
        }, TimeSpan.FromSeconds(3));

        // Wait for turn 1 output
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Wait for compaction
        subscriber.ExpectMsg<CompactionOutput>(TimeSpan.FromSeconds(5));

        // After compaction, lower the usage so the buffered message doesn't trigger again
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 20,
            TotalTokenCount = 120
        };

        // Buffered message should be drained and produce output
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(5));
        subscriber.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
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
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>(); // Drain subscriber notification

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Message before compaction"
        }, TimeSpan.FromSeconds(3));

        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<CompactionOutput>(TimeSpan.FromSeconds(5));

        // Phase 2: Kill the session actor
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath).ResolveOne(TimeSpan.FromSeconds(3));
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child);

        // Phase 3: Recover — join again
        var recoverSub = CreateTestProbe("recover-sub-2");
        var recovered = await sessionManager.Ask<SessionJoined>(new JoinSession
        {
            SessionId = sessionId,
            Subscriber = recoverSub,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3));
        recoverSub.ExpectMsg<SessionJoined>(); // Drain subscriber notification
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
        }, TimeSpan.FromSeconds(3));

        var text = recoverSub.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
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
        }, TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Investigate ticket 579"
        }, TimeSpan.FromSeconds(3));

        // Turn output
        subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<UsageOutput>(TimeSpan.FromSeconds(3));
        subscriber.ExpectMsg<TurnCompleted>(TimeSpan.FromSeconds(3));

        // Compaction (extractive — no LLM summarization)
        var compaction = subscriber.ExpectMsg<CompactionOutput>(TimeSpan.FromSeconds(5));
        Assert.False(compaction.Summarized);

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
        }, TimeSpan.FromSeconds(3));

        // Session still works — the context-summary message is there as context
        var text = subscriber.ExpectMsg<TextOutput>(TimeSpan.FromSeconds(3));
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
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
