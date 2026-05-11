// -----------------------------------------------------------------------
// <copyright file="SessionStateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Pure unit tests for <see cref="SessionState"/>. No ActorSystem needed —
/// tests immutable state transitions in isolation.
/// </summary>
public class SessionStateTests
{
    private static readonly SessionId TestSessionId = new("test/session");

    [Fact]
    public void Empty_state_has_no_history()
    {
        var state = SessionState.Empty;
        Assert.Empty(state.History);
        Assert.Equal(0, state.TurnCount);
        Assert.Null(state.Title);
    }

    [Fact]
    public void Apply_TurnRecorded_adds_messages_and_increments_turn()
    {
        var state = SessionState.Empty;
        var evt = new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Hello" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Hi there" },
            RecordedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var next = state.Apply(evt);

        Assert.Equal(2, next.History.Count);
        Assert.Equal(ChatRole.User, next.History[0].Role);
        Assert.Equal("Hello", next.History[0].Content);
        Assert.Equal(ChatRole.Assistant, next.History[1].Role);
        Assert.Equal("Hi there", next.History[1].Content);
        Assert.Equal(1, next.TurnCount);
    }

    [Fact]
    public void Apply_TurnRecorded_is_cumulative()
    {
        var state = SessionState.Empty;

        state = state.Apply(new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "First" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Reply 1" },
        });
        state = state.Apply(new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Second" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Reply 2" },
        });

        Assert.Equal(4, state.History.Count);
        Assert.Equal(2, state.TurnCount);
    }

    [Fact]
    public void Apply_SessionTitleSet_updates_title()
    {
        var state = SessionState.Empty;
        var next = state.Apply(new SessionTitleSet
        {
            SessionId = TestSessionId,
            Title = "My conversation"
        });

        Assert.Equal("My conversation", next.Title);
    }

    [Fact]
    public void Apply_SessionCompacted_preserves_system_prompt()
    {
        var state = WithSystemPrompt("System prompt")
            .Apply(new TurnRecorded
            {
                SessionId = TestSessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Hello" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Hi" },
            })
            .Apply(new TurnRecorded
            {
                SessionId = TestSessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "More" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Sure" },
            });

        Assert.Equal(5, state.History.Count); // system + 2 turns

        var compacted = state.Apply(new SessionCompacted
        {
            SessionId = TestSessionId,
            Summary = "Conversation summary",
            CompactedMessages =
            [
                new() { Role = ChatRole.Assistant, Content = "Summary of prior conversation." }
            ]
        });

        // System prompt preserved + compacted message
        Assert.Equal(2, compacted.History.Count);
        Assert.Equal(ChatRole.System, compacted.History[0].Role);
        Assert.Equal("System prompt", compacted.History[0].Content);
        Assert.Equal(ChatRole.Assistant, compacted.History[1].Role);
        Assert.Equal("Summary of prior conversation.", compacted.History[1].Content);
    }

    [Fact]
    public void Apply_SessionCompacted_without_system_prompt_uses_only_compacted_messages()
    {
        var state = SessionState.Empty
            .Apply(new TurnRecorded
            {
                SessionId = TestSessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Hello" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Hi" },
            });

        var compacted = state.Apply(new SessionCompacted
        {
            SessionId = TestSessionId,
            CompactedMessages =
            [
                new() { Role = ChatRole.Assistant, Content = "Compacted" }
            ]
        });

        Assert.Single(compacted.History);
        Assert.Equal("Compacted", compacted.History[0].Content);
    }

    [Fact]
    public void AddUserMessage_appends_to_history()
    {
        var state = SessionState.Empty;
        var next = state.AddUserMessage("Hello world");

        Assert.Single(next.History);
        Assert.Equal(ChatRole.User, next.History[0].Role);
        Assert.Equal("Hello world", next.History[0].Content);
        Assert.Equal(0, next.TurnCount); // turn count not incremented
    }

    [Fact]
    public void AddErrorReply_appends_and_increments_turn()
    {
        var state = SessionState.Empty.AddUserMessage("Hello");
        var next = state.AddErrorReply("Something went wrong");

        Assert.Equal(2, next.History.Count);
        Assert.Equal(ChatRole.Assistant, next.History[1].Role);
        Assert.Equal("Something went wrong", next.History[1].Content);
        Assert.Equal(1, next.TurnCount);
    }

    [Fact]
    public void FindLastUserMessage_returns_most_recent_user_message()
    {
        var state = WithSystemPrompt("System")
            .AddUserMessage("First")
            .Apply(new TurnRecorded
            {
                SessionId = TestSessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "First" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Reply" },
            })
            .AddUserMessage("Second");

        var lastUser = state.FindLastUserMessage();
        Assert.NotNull(lastUser);
        Assert.Equal("Second", lastUser.Content);
    }

    [Fact]
    public void FindLastUserMessage_skips_transient_system_nudges()
    {
        var state = SessionState.Empty
            .AddUserMessage("Real user message")
            .AddSystemNudge("You produced an empty response.");

        var lastUser = state.FindLastUserMessage();

        Assert.NotNull(lastUser);
        Assert.Equal("Real user message", lastUser.Content);
    }

    [Fact]
    public void FindLastUserMessage_returns_null_when_no_user_messages()
    {
        var state = WithSystemPrompt("System");

        Assert.Null(state.FindLastUserMessage());
    }

    [Fact]
    public void ToSnapshot_and_FromSnapshot_round_trip()
    {
        var state = WithSystemPrompt("Prompt")
            .Apply(new TurnRecorded
            {
                SessionId = TestSessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Hello" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Hi" },
            })
            .Apply(new SessionTitleSet { SessionId = TestSessionId, Title = "Test title" });

        var snapshot = state.ToSnapshot();
        var restored = SessionState.FromSnapshot(snapshot);

        Assert.Equal(state.History.Count, restored.History.Count);
        Assert.Equal(state.TurnCount, restored.TurnCount);
        Assert.Equal(state.Title, restored.Title);

        for (int i = 0; i < state.History.Count; i++)
        {
            Assert.Equal(state.History[i].Role, restored.History[i].Role);
            Assert.Equal(state.History[i].Content, restored.History[i].Content);
        }
    }

    [Fact]
    public void State_is_immutable_original_not_modified()
    {
        var original = SessionState.Empty;
        var modified = original.AddUserMessage("Hello");

        Assert.Empty(original.History);
        Assert.Single(modified.History);
    }

    [Fact]
    public void Apply_SessionCompacted_retains_existing_WorkingContext_when_event_has_none()
    {
        // Compaction preserves WorkingContext across the event boundary.
        // When the event's WorkingContext is null (the common case — the
        // event is just transporting the compacted messages, not a
        // working-context update), the existing WorkingContext on state
        // is retained.
        var state = WithSystemPrompt("System") with
        {
            WorkingContext = WorkingContext.Empty
                .AddRecentFile("src/Rect.cs")
                .AddRecentFile("src/Thickness.cs")
        };

        var compacted = state.Apply(new SessionCompacted
        {
            SessionId = TestSessionId,
            CompactedMessages =
            [
                new() { Role = ChatRole.User, Content = "[session-summary session:test/session]\nsummary" }
            ],
            WorkingContext = null  // event does not carry an update
        });

        Assert.Equal(2, compacted.WorkingContext.RecentFiles.Count);
        Assert.Equal("src/Thickness.cs", compacted.WorkingContext.RecentFiles[0]);
        Assert.Equal("src/Rect.cs", compacted.WorkingContext.RecentFiles[1]);
    }

    [Fact]
    public void Apply_SessionCompacted_replaces_WorkingContext_when_event_has_update()
    {
        // When a compaction event carries a WorkingContext field, the
        // new state takes that value (replacing whatever was there).
        var state = WithSystemPrompt("System") with
        {
            WorkingContext = WorkingContext.Empty.AddRecentFile("src/Old.cs")
        };

        var newContext = WorkingContext.Empty
            .AddRecentFile("src/New1.cs")
            .AddRecentFile("src/New2.cs");

        var compacted = state.Apply(new SessionCompacted
        {
            SessionId = TestSessionId,
            CompactedMessages =
            [
                new() { Role = ChatRole.User, Content = "[session-summary session:test/session]\nsummary" }
            ],
            WorkingContext = newContext
        });

        Assert.Equal(new[] { "src/New2.cs", "src/New1.cs" }, compacted.WorkingContext.RecentFiles);
        Assert.DoesNotContain("src/Old.cs", compacted.WorkingContext.RecentFiles);
    }

    [Fact]
    public void ToSnapshot_and_FromSnapshot_round_trip_preserves_WorkingContext()
    {
        // Actor recovery path: a session with populated WorkingContext
        // must survive a snapshot + rehydrate cycle. This is the
        // property the spec scenario "WorkingContext survives actor
        // recovery" depends on.
        var state = WithSystemPrompt("System") with
        {
            WorkingContext = WorkingContext.Empty
                .AddRecentFile("src/A.cs")
                .AddRecentFile("src/B.cs")
                .AddRecentFile("src/C.cs")
        };

        var snapshot = state.ToSnapshot();
        var restored = SessionState.FromSnapshot(snapshot);

        Assert.Equal(
            new[] { "src/C.cs", "src/B.cs", "src/A.cs" },
            restored.WorkingContext.RecentFiles);
    }

    [Fact]
    public void FromSnapshot_with_null_WorkingContext_defaults_to_empty()
    {
        // Backward-compat: snapshots written before this change have
        // no WorkingContext field; rehydration must produce
        // WorkingContext.Empty, not null.
        var snapshot = new SessionSnapshot
        {
            History = [],
            TurnCount = 0,
            Title = null,
            WorkingContext = null
        };

        var restored = SessionState.FromSnapshot(snapshot);

        Assert.NotNull(restored.WorkingContext);
        Assert.True(restored.WorkingContext.IsEmpty);
        Assert.Same(WorkingContext.Empty, restored.WorkingContext);
    }

    [Fact]
    public void Empty_WorkingContext_survives_snapshot_round_trip()
    {
        // Behavioral property: a session with empty WorkingContext
        // round-trips through snapshot + rehydrate producing a state
        // whose WorkingContext is still IsEmpty. Doesn't assert the
        // on-wire representation (null vs empty record) — that's an
        // implementation detail.
        var state = WithSystemPrompt("System");
        var restored = SessionState.FromSnapshot(state.ToSnapshot());

        Assert.True(restored.WorkingContext.IsEmpty);
    }

    [Fact]
    public void ToSnapshot_and_FromSnapshot_round_trip_preserves_AdoptedContextRecords()
    {
        var timestamp = new DateTimeOffset(2026, 4, 28, 12, 0, 0, TimeSpan.Zero);
        var state = SessionState.Empty.Apply(new AdoptedContextRecorded
        {
            SessionId = TestSessionId,
            AuthorizedMessageId = "authorized-1",
            AuthorizerSenderId = "user-1",
            LowerBound = "cursor-0",
            UpperBound = "authorized-1",
            Projection = "[adopted-context]",
            HasAdoptedContext = true,
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = ["user-1", "observer-1"],
            ProjectionPersisted = true,
            Messages =
            [
                new AdoptedContextRecorded.AdoptedMessageRecord
                {
                    MessageId = "history-1",
                    SenderId = "observer-1",
                    TimestampMs = timestamp.ToUnixTimeMilliseconds(),
                    AuthorityAtInclusion = "pending"
                }
            ]
        });

        var restored = SessionState.FromSnapshot(state.ToSnapshot());

        var record = Assert.Single(restored.AdoptedContextRecords);
        Assert.Equal("authorized-1", record.Key);
        Assert.Equal("user-1", record.Value.AuthorizerSenderId);
        Assert.Equal("cursor-0", record.Value.LowerBound);
        Assert.Equal("authorized-1", record.Value.UpperBound);
        Assert.True(record.Value.HasAdoptedContext);
        Assert.True(record.Value.HasThirdPartyAdoptedContext);
        Assert.Equal(["user-1", "observer-1"], record.Value.AdoptedSpeakerIds);
        Assert.True(record.Value.ProjectionPersisted);
        Assert.Equal("[adopted-context]", record.Value.Projection);
        var message = Assert.Single(record.Value.Messages);
        Assert.Equal("history-1", message.MessageId);
        Assert.Equal("observer-1", message.SenderId);
        Assert.Equal(timestamp, message.Timestamp);
        Assert.Equal("pending", message.AuthorityAtInclusion);
    }

    [Fact]
    public void Apply_TurnRecorded_folds_SourceReminderId_into_ProcessedReminderIds()
    {
        var state = SessionState.Empty;
        var evt = new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "check PR" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "merged" },
            SourceReminderId = "check-pr:1712000000000"
        };

        var next = state.Apply(evt);

        Assert.Contains("check-pr:1712000000000", next.ProcessedReminderIds);
        Assert.Single(next.ProcessedReminderIds);
    }

    [Fact]
    public void Apply_TurnRecorded_with_null_SourceReminderId_does_not_grow_set()
    {
        var state = SessionState.Empty
            .Apply(new TurnRecorded
            {
                SessionId = TestSessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "hi" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "hello" },
                SourceReminderId = null
            });

        Assert.Empty(state.ProcessedReminderIds);
    }

    [Fact]
    public void Apply_TurnRecorded_replay_builds_cumulative_dedup_set()
    {
        var state = SessionState.Empty;

        state = state.Apply(new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "r1" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "ok" },
            SourceReminderId = "r1:100"
        });
        state = state.Apply(new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "user turn" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "ok" },
            SourceReminderId = null
        });
        state = state.Apply(new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "r2" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "ok" },
            SourceReminderId = "r2:200"
        });

        Assert.Equal(2, state.ProcessedReminderIds.Count);
        Assert.Contains("r1:100", state.ProcessedReminderIds);
        Assert.Contains("r2:200", state.ProcessedReminderIds);
    }

    [Fact]
    public void Apply_SessionCompacted_preserves_ProcessedReminderIds()
    {
        var state = SessionState.Empty
            .Apply(new TurnRecorded
            {
                SessionId = TestSessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "r1" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "ok" },
                SourceReminderId = "preserved:1"
            });

        var compacted = state.Apply(new SessionCompacted
        {
            SessionId = TestSessionId,
            CompactedMessages =
            [
                new() { Role = ChatRole.User, Content = "[session-summary session:test/session]\nsummary" }
            ]
        });

        Assert.Contains("preserved:1", compacted.ProcessedReminderIds);
    }

    [Fact]
    public void ProcessedReminderIds_is_not_persisted_in_snapshot()
    {
        // Explicit verification of the "in-memory only" contract: a state
        // with a populated dedup set round-trips through ToSnapshot/
        // FromSnapshot with an empty set on the restored side. This is
        // the accepted tradeoff — duplicates across snapshot recovery
        // are tolerable.
        var state = SessionState.Empty
            .Apply(new TurnRecorded
            {
                SessionId = TestSessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "r1" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "ok" },
                SourceReminderId = "lost-on-snapshot:1"
            });

        Assert.NotEmpty(state.ProcessedReminderIds);

        var restored = SessionState.FromSnapshot(state.ToSnapshot());

        Assert.Empty(restored.ProcessedReminderIds);
    }

    private static SessionState WithSystemPrompt(string content)
    {
        return SessionState.Empty with
        {
            History = ImmutableList.Create(
                new SerializableChatMessage { Role = ChatRole.System, Content = content })
        };
    }
}
