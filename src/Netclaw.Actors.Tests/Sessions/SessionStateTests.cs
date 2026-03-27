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
            CompactedMessages = new List<SerializableChatMessage>
            {
                new() { Role = ChatRole.Assistant, Content = "Summary of prior conversation." }
            }
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
            CompactedMessages = new List<SerializableChatMessage>
            {
                new() { Role = ChatRole.Assistant, Content = "Compacted" }
            }
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

    private static SessionState WithSystemPrompt(string content)
    {
        return SessionState.Empty with
        {
            History = ImmutableList.Create(
                new SerializableChatMessage { Role = ChatRole.System, Content = content })
        };
    }
}
