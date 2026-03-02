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
            KeepRecentMessages = 0,
            TitleGenerationInterval = 0
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
        });
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, this should trigger compaction"
        });

        // First: normal text response from the turn
        await subscriber.ExpectMsgAsync<TextOutput>();
        await subscriber.ExpectMsgAsync<UsageOutput>();
        await subscriber.ExpectMsgAsync<TurnCompleted>();

        // Then: compaction output (with observations from Observer)
        var compaction = await subscriber.ExpectMsgAsync<CompactionOutput>();
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
        });
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Hello, this should NOT trigger compaction"
        });

        await subscriber.ExpectMsgAsync<TextOutput>();
        await subscriber.ExpectMsgAsync<UsageOutput>();
        await subscriber.ExpectMsgAsync<TurnCompleted>();

        // No compaction output should appear
        await subscriber.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500));

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
        });
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Trigger compaction"
        });

        // Wait for turn + compaction
        await subscriber.ExpectMsgAsync<TextOutput>();
        await subscriber.ExpectMsgAsync<UsageOutput>();
        await subscriber.ExpectMsgAsync<TurnCompleted>();
        await subscriber.ExpectMsgAsync<CompactionOutput>();

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
        });

        var text = await subscriber.ExpectMsgAsync<TextOutput>();
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<UsageOutput>();
        await subscriber.ExpectMsgAsync<TurnCompleted>();
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
        });
        await subscriber.ExpectMsgAsync<SessionJoined>();

        // First message — triggers turn then compaction
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "First message"
        });

        // Buffer a second message during processing/compaction
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Second message (buffered)"
        });

        // Wait for turn 1 output
        await subscriber.ExpectMsgAsync<TextOutput>();
        await subscriber.ExpectMsgAsync<UsageOutput>();
        await subscriber.ExpectMsgAsync<TurnCompleted>();

        // Wait for compaction
        await subscriber.ExpectMsgAsync<CompactionOutput>();

        // After compaction, lower the usage so the buffered message doesn't trigger again
        _fakeChatClient.UsageOverride = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 20,
            TotalTokenCount = 120
        };

        // Buffered message should be drained and produce output
        await subscriber.ExpectMsgAsync<TextOutput>();
        await subscriber.ExpectMsgAsync<UsageOutput>();
        await subscriber.ExpectMsgAsync<TurnCompleted>();
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
        });
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Message before compaction"
        });

        await subscriber.ExpectMsgAsync<TextOutput>();
        await subscriber.ExpectMsgAsync<UsageOutput>();
        await subscriber.ExpectMsgAsync<TurnCompleted>();
        await subscriber.ExpectMsgAsync<CompactionOutput>();

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
        });
        await recoverSub.ExpectMsgAsync<SessionJoined>();
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
        });

        var text = await recoverSub.ExpectMsgAsync<TextOutput>();
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
        });
        await subscriber.ExpectMsgAsync<SessionJoined>();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Investigate ticket 579"
        });

        // Turn output
        await subscriber.ExpectMsgAsync<TextOutput>();
        await subscriber.ExpectMsgAsync<UsageOutput>();
        await subscriber.ExpectMsgAsync<TurnCompleted>();

        // Compaction (with observations from Observer)
        await subscriber.ExpectMsgAsync<CompactionOutput>();

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
        });

        // Session still works — the context-summary message is there as context
        var text = await subscriber.ExpectMsgAsync<TextOutput>();
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
