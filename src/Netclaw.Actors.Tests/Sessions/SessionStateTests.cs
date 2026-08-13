// -----------------------------------------------------------------------
// <copyright file="SessionStateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

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
    public void Apply_TurnRecorded_restores_all_batched_user_messages()
    {
        var state = SessionState.Empty.Apply(new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Second" },
            UserMessages =
            [
                new SerializableChatMessage { Role = ChatRole.User, Content = "First" },
                new SerializableChatMessage { Role = ChatRole.User, Content = "Second" }
            ],
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Done" },
            RecordedAtMs = 42
        });

        Assert.Equal(
            ["First", "Second", "Done"],
            state.History.Select(message => message.Content));
        Assert.Equal(
            [SessionTranscriptEntryTypes.User, SessionTranscriptEntryTypes.User, SessionTranscriptEntryTypes.Assistant],
            state.RecentTranscript.Select(entry => entry.Type));
        Assert.Equal(
            ["First", "Second", "Done"],
            state.RecentTranscript.Select(entry => entry.Text));
        Assert.Equal(1, state.TurnCount);
    }

    [Fact]
    public void Turn_checkpoint_keeps_all_batched_user_messages()
    {
        var turn = new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Second" },
            UserMessages =
            [
                new SerializableChatMessage { Role = ChatRole.User, Content = "First" },
                new SerializableChatMessage { Role = ChatRole.User, Content = "Second" }
            ],
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Done" }
        };

        var checkpoint = SessionMemoryCheckpointFactory.ForTurnComplete(
            TestSessionId,
            turn,
            "trusted-instance",
            "personal");

        Assert.Equal("First\n\nSecond", checkpoint.UserContent);
        Assert.Equal("User: First\n\nSecond\nAssistant: Done", checkpoint.Content);
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
    public void Apply_legacy_TurnRecorded_derives_settled_transcript_entries()
    {
        var state = SessionState.Empty.Apply(new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Hello" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Hi" },
            RecordedAtMs = 42
        });

        Assert.Collection(
            state.RecentTranscript,
            entry =>
            {
                Assert.Equal(SessionTranscriptEntryTypes.User, entry.Type);
                Assert.Equal("Hello", entry.Text);
                Assert.Equal(42, entry.TimestampMs);
            },
            entry =>
            {
                Assert.Equal(SessionTranscriptEntryTypes.Assistant, entry.Type);
                Assert.Equal("Hi", entry.Text);
                Assert.Equal(42, entry.TimestampMs);
            });
    }

    [Fact]
    public void Apply_TurnRecorded_uses_explicit_transcript_entries()
    {
        var expected = new SessionTranscriptEntry
        {
            Type = SessionTranscriptEntryTypes.Tool,
            TurnId = "turn-1",
            CallId = "call-1",
            ToolName = "shell_execute",
            Result = "ok"
        };
        var state = SessionState.Empty.Apply(new TurnRecorded
        {
            SessionId = TestSessionId,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Run it" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Done" },
            TranscriptEntries = [expected]
        });

        Assert.Equal([expected], state.RecentTranscript);
    }

    [Fact]
    public void KeepRecentTranscriptTurns_keeps_complete_recent_turns()
    {
        var state = SessionState.Empty;
        for (var turn = 1; turn <= 3; turn++)
        {
            state = state.Apply(new TurnRecorded
            {
                SessionId = TestSessionId,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = $"User {turn}" },
                AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = $"Reply {turn}" }
            });
        }

        var result = state.KeepRecentTranscriptTurns(2);

        Assert.Equal(4, result.RecentTranscript.Count);
        Assert.Equal("User 2", result.RecentTranscript[0].Text);
        Assert.Equal("Reply 3", result.RecentTranscript[^1].Text);
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
    public void AddSystemNudge_can_carry_media_without_becoming_last_user_message()
    {
        var media = new SerializableMediaReference
        {
            RelativePath = "image.png",
            MimeType = new Netclaw.Media.MimeType("image/png"),
            Modality = (int)MediaModality.Image,
            FileSizeBytes = 16
        };
        var state = SessionState.Empty
            .AddUserMessage("Real user message")
            .AddSystemNudge("Loaded media.", [media]);

        var nudge = state.History[^1];
        Assert.Single(nudge.MediaReferences);
        Assert.Equal("Real user message", state.FindLastUserMessage()?.Content);
    }

    [Fact]
    public void AddSystemNudge_snapshots_media_so_caller_clear_cannot_empty_it()
    {
        // Regression: LlmSessionActor hands a caller-owned media accumulator
        // (ModelInputMediaBuffer) to AddSystemNudge and then reuses/empties it.
        // Without a defensive snapshot the nudge would alias that list, so the
        // caller's reuse wiped the tool-loaded image before the next LLM call
        // hydrated it — the model was told "Image loaded" but never saw the bytes
        // and hallucinated. The nudge must retain its own copy.
        var media = new SerializableMediaReference
        {
            RelativePath = "image.png",
            MimeType = new Netclaw.Media.MimeType("image/png"),
            Modality = (int)MediaModality.Image,
            FileSizeBytes = 16
        };
        var pending = new List<SerializableMediaReference> { media };

        var state = SessionState.Empty
            .AddUserMessage("Real user message")
            .AddSystemNudge("Loaded media.", pending);

        pending.Clear();

        Assert.Single(state.History[^1].MediaReferences);
    }

    [Fact]
    public void AddUserMessage_snapshots_media_so_caller_clear_cannot_empty_it()
    {
        var media = new SerializableMediaReference
        {
            RelativePath = "image.png",
            MimeType = new Netclaw.Media.MimeType("image/png"),
            Modality = (int)MediaModality.Image,
            FileSizeBytes = 16
        };
        var pending = new List<SerializableMediaReference> { media };

        var state = SessionState.Empty.AddUserMessage("With image", pending);

        pending.Clear();

        Assert.Single(state.History[^1].MediaReferences);
    }

    [Fact]
    public void FindLastUserMessage_returns_null_when_no_user_messages()
    {
        var state = WithSystemPrompt("System");

        Assert.Null(state.FindLastUserMessage());
    }

    [Fact]
    public void AddVolatileContextNudge_inserts_before_trailing_real_user_message()
    {
        // The volatile context block must NOT sit at the tail after the real
        // user message — a trailing volatile User-role message is read by
        // strict ChatML templates as a fresh user turn and triggers the
        // tool-loop acknowledgement spin. It is inserted BEFORE the user
        // message so the real user message stays at the tail.
        var state = SessionState.Empty
            .AddUserMessage("real user request")
            .AddVolatileContextNudge("[memory-recall] ...");

        Assert.Equal(2, state.History.Count);
        Assert.True(SessionState.IsSystemNudge(state.History[0]));
        Assert.Equal(ChatRole.User, state.History[1].Role);
        Assert.Equal("real user request", state.History[1].Content);

        // The last user-role content the model sees is the real request.
        Assert.Equal("real user request", state.FindLastUserMessage()?.Content);
    }

    [Fact]
    public void AddVolatileContextNudge_appends_when_no_trailing_real_user_message()
    {
        // Reminder/scheduled/cold-recovery turns have no trailing real user
        // message to sit before — append in that case. Also covers the case
        // where the last entry is itself a system-nudge (e.g. delivery retry).
        var afterAssistant = SessionState.Empty
            .AddUserMessage("first")
            .AddErrorReply("answer")
            .AddVolatileContextNudge("[working-context] ...");

        Assert.True(SessionState.IsSystemNudge(afterAssistant.History[^1]));

        var afterNudge = SessionState.Empty
            .AddUserMessage("first")
            .AddSystemNudge("delivery retry")
            .AddVolatileContextNudge("[working-context] ...");

        Assert.True(SessionState.IsSystemNudge(afterNudge.History[^1]));
        Assert.Contains("[working-context]", afterNudge.History[^1].Content);
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
    public void From_legacy_snapshot_derives_tool_transcript_without_active_state()
    {
        var snapshot = new SessionSnapshot
        {
            TurnCount = 1,
            History =
            [
                new SerializableChatMessage { Role = ChatRole.User, Content = "Check status" },
                new SerializableChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = string.Empty,
                    ToolCalls =
                    [
                        new SerializableToolCall
                        {
                            CallId = new ToolCallId("call-1"),
                            Name = new ToolName("status"),
                            ArgumentsJson = "{}"
                        }
                    ]
                },
                new SerializableChatMessage
                {
                    Role = ChatRole.Tool,
                    Content = "healthy",
                    Name = "status",
                    ToolCallId = new ToolCallId("call-1")
                },
                new SerializableChatMessage { Role = ChatRole.Assistant, Content = "All healthy" }
            ]
        };

        var restored = SessionState.FromSnapshot(snapshot);

        var tool = Assert.Single(restored.RecentTranscript, entry =>
            entry.Type == SessionTranscriptEntryTypes.Tool);
        Assert.Equal("call-1", tool.CallId);
        Assert.Equal("healthy", tool.Result);
        Assert.DoesNotContain(restored.RecentTranscript, entry =>
            entry.Type == SessionTranscriptEntryTypes.Diagnostic);
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
            AuthorizerSenderId = new SenderId("user-1"),
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
                    SenderId = new SenderId("observer-1"),
                    TimestampMs = timestamp.ToUnixTimeMilliseconds(),
                    AuthorityAtInclusion = "pending"
                }
            ]
        });

        var restored = SessionState.FromSnapshot(state.ToSnapshot());

        var record = Assert.Single(restored.AdoptedContextRecords);
        Assert.Equal("authorized-1", record.Key);
        Assert.Equal("user-1", record.Value.AuthorizerSenderId?.Value);
        Assert.Equal("cursor-0", record.Value.LowerBound);
        Assert.Equal("authorized-1", record.Value.UpperBound);
        Assert.True(record.Value.HasAdoptedContext);
        Assert.True(record.Value.HasThirdPartyAdoptedContext);
        Assert.Equal(["user-1", "observer-1"], record.Value.AdoptedSpeakerIds);
        Assert.True(record.Value.ProjectionPersisted);
        Assert.Equal("[adopted-context]", record.Value.Projection);
        var message = Assert.Single(record.Value.Messages);
        Assert.Equal("history-1", message.MessageId);
        Assert.Equal("observer-1", message.SenderId.Value);
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
            SourceReminderId = new ReminderId("check-pr:1712000000000")
        };

        var next = state.Apply(evt);

        Assert.Contains(new ReminderId("check-pr:1712000000000"), next.ProcessedReminderIds);
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
            SourceReminderId = new ReminderId("r1:100")
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
            SourceReminderId = new ReminderId("r2:200")
        });

        Assert.Equal(2, state.ProcessedReminderIds.Count);
        Assert.Contains(new ReminderId("r1:100"), state.ProcessedReminderIds);
        Assert.Contains(new ReminderId("r2:200"), state.ProcessedReminderIds);
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
                SourceReminderId = new ReminderId("preserved:1")
            });

        var compacted = state.Apply(new SessionCompacted
        {
            SessionId = TestSessionId,
            CompactedMessages =
            [
                new() { Role = ChatRole.User, Content = "[session-summary session:test/session]\nsummary" }
            ]
        });

        Assert.Contains(new ReminderId("preserved:1"), compacted.ProcessedReminderIds);
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
                SourceReminderId = new ReminderId("lost-on-snapshot:1")
            });

        Assert.NotEmpty(state.ProcessedReminderIds);

        var restored = SessionState.FromSnapshot(state.ToSnapshot());

        Assert.Empty(restored.ProcessedReminderIds);
    }

    [Fact]
    public void Successful_subagent_merge_adds_only_confirmed_changed_files()
    {
        var child = new WorkingContextDelta
        {
            ReadFiles = ["src/ReadOnly.cs"],
            ConfirmedChangedFiles = ["src/Changed.cs"],
            ObservedChangedFiles = ["src/ObservedOnly.cs"]
        };

        var merged = LlmSessionActor.MergeSuccessfulSubAgentWorkingContext(
            WorkingContext.Empty,
            new ChildRunCompletion.Completed(child));

        Assert.Equal(["src/Changed.cs"], merged.RecentFiles);
    }

    [Fact]
    public void Failed_subagent_merge_does_not_change_parent_working_context()
    {
        var current = WorkingContext.Empty.AddRecentFile("src/Existing.cs");
        var child = new WorkingContextDelta
        {
            ConfirmedChangedFiles = ["src/Denied.cs"]
        };

        var merged = LlmSessionActor.MergeSuccessfulSubAgentWorkingContext(
            current,
            new ChildRunCompletion.Failed(SubAgentOutcomeReason.ToolExecutionFailed));

        Assert.Same(current, merged);
    }

    [Fact]
    public void Cancelled_subagent_cannot_supply_parent_working_context_changes()
    {
        var current = WorkingContext.Empty.AddRecentFile("src/Existing.cs");

        var merged = LlmSessionActor.MergeSuccessfulSubAgentWorkingContext(
            current,
            new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent));

        Assert.Same(current, merged);
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
