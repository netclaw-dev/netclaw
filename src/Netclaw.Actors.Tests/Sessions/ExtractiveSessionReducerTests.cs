using Microsoft.Extensions.AI;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Unit tests for <see cref="ExtractiveSessionReducer"/>.
/// Verifies system prompt preservation, last-N retention, tool message handling,
/// and edge cases (empty, zero-keep, negative-keep, at-limit no-op).
/// </summary>
public class ExtractiveSessionReducerTests
{
    [Fact]
    public async Task System_prompt_is_always_preserved()
    {
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 2);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are helpful."),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi there!"),
            new(ChatRole.User, "How are you?"),
            new(ChatRole.Assistant, "I'm good!")
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(3, result.Count); // system + 2 kept
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Equal("You are helpful.", result[0].Text);
    }

    [Fact]
    public async Task Keeps_last_N_non_system_messages()
    {
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 2);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "First"),
            new(ChatRole.Assistant, "Reply 1"),
            new(ChatRole.User, "Second"),
            new(ChatRole.Assistant, "Reply 2")
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal("Second", result[1].Text);
        Assert.Equal("Reply 2", result[2].Text);
    }

    [Fact]
    public async Task Tool_messages_kept_within_window()
    {
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 4);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Old message"),
            new(ChatRole.Assistant, "Old reply"),
            // These 4 should be kept:
            new(ChatRole.User, "Search for X"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = "X" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "Found X")]),
            new(ChatRole.Assistant, "Here's what I found about X.")
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(5, result.Count); // system + 4 kept
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Equal("Search for X", result[1].Text);
        Assert.Equal(ChatRole.Tool, result[3].Role);
        Assert.Equal("Here's what I found about X.", result[4].Text);
    }

    [Fact]
    public async Task Empty_history_returns_empty()
    {
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 5);
        var messages = new List<ChatMessage>();

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public async Task Keep_zero_preserves_only_system_prompt()
    {
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 0);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!")
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Equal(ChatRole.System, result[0].Role);
    }

    [Fact]
    public async Task Negative_keep_treated_as_zero()
    {
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: -5);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!")
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Equal(ChatRole.System, result[0].Role);
    }

    [Fact]
    public async Task At_limit_returns_original_messages()
    {
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 4);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!"),
            new(ChatRole.User, "Bye"),
            new(ChatRole.Assistant, "Goodbye!")
        };

        var result = await reducer.ReduceAsync(messages, CancellationToken.None);

        // At limit — no reduction needed, returns original reference
        Assert.Same(messages, result);
    }

    [Fact]
    public async Task Over_limit_returns_original_messages()
    {
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 10);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!")
        };

        var result = await reducer.ReduceAsync(messages, CancellationToken.None);

        // Under limit — no reduction needed, returns original reference
        Assert.Same(messages, result);
    }

    [Fact]
    public async Task No_system_prompt_keeps_last_N()
    {
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 2);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "First"),
            new(ChatRole.Assistant, "Reply 1"),
            new(ChatRole.User, "Second"),
            new(ChatRole.Assistant, "Reply 2")
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("Second", result[0].Text);
        Assert.Equal("Reply 2", result[1].Text);
    }

    [Fact]
    public async Task Window_walks_backward_to_user_boundary_when_naive_cut_would_orphan_tool_result()
    {
        // keepCount=3 would place the naive cut on the Tool result, whose
        // matching FunctionCallContent is in the discarded portion. The
        // reducer must walk backward to the preceding User-role message.
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 3);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Old user turn"),                                                 // index 1, discarded
            new(ChatRole.Assistant, "Old assistant reply"),                                      // index 2, discarded
            new(ChatRole.User, "Search for X"),                                                   // index 3, user boundary
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = "X" })]), // 4
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "Found X")]),               // 5, naive cut lands here
            new(ChatRole.Assistant, "Let me check further.")                                    // 6
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        // Expect: system + user "Search for X" + assistant tool_call + tool result + assistant
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Equal("Search for X", result[1].Text);
        Assert.Contains(result[2].Contents, c => c is FunctionCallContent);
        Assert.Equal(ChatRole.Tool, result[3].Role);
        Assert.Equal("Let me check further.", result[4].Text);
    }

    [Fact]
    public async Task Window_walks_backward_past_assistant_tool_call_to_user_boundary()
    {
        // keepCount=2 naive cut lands on the Assistant tool_call. The reducer
        // must walk back to the preceding User-role message so the tool_call
        // is contextualized by the user turn that requested it.
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 2);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Old user turn"),
            new(ChatRole.Assistant, "Old assistant reply"),
            new(ChatRole.User, "Please grep for foo"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-a", "grep", new Dictionary<string, object?> { ["pattern"] = "foo" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-a", "3 matches")])
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Equal("Please grep for foo", result[1].Text);
        Assert.Contains(result[2].Contents, c => c is FunctionCallContent);
        Assert.Equal(ChatRole.Tool, result[3].Role);
    }

    [Fact]
    public async Task Window_skips_system_nudges_when_finding_user_boundary()
    {
        // A system nudge uses User role but has the SystemNudgePrefix — it's
        // actor-injected (recall content, empty-response nudges), not a real
        // user turn. The backward walk must skip it.
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 2);
        var nudgeContent = $"{SessionState.SystemNudgePrefix} recalled-memory blah]";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Real user turn"),
            new(ChatRole.Assistant, "Reply"),
            new(ChatRole.User, nudgeContent),                                     // nudge — not a real user turn
            new(ChatRole.Assistant, [new FunctionCallContent("call-x", "tool", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-x", "done")])
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        // Walk should skip the nudge and land on "Real user turn".
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Equal("Real user turn", result[1].Text);
    }

    [Fact]
    public async Task Window_start_already_on_user_boundary_is_preserved()
    {
        // Naive cut lands on a User message — no walk needed.
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 3);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Discarded"),
            new(ChatRole.Assistant, "Discarded reply"),
            new(ChatRole.User, "Kept user turn"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")])
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(4, result.Count);
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Equal("Kept user turn", result[1].Text);
    }

    [Fact]
    public async Task Window_falls_back_to_keep_all_post_system_when_no_user_message_found()
    {
        // Degenerate case: no user message post-system, first post-system
        // message is Assistant (not Tool). Keep everything post-system —
        // the defense-in-depth advance-forward only triggers on leading Tool
        // orphans, which this history doesn't have.
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 2);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.Assistant, "Assistant only"),
            new(ChatRole.Assistant, [new FunctionCallContent("c", "t", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("c", "r")]),
            new(ChatRole.Assistant, "More assistant")
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(5, result.Count);
        Assert.Equal(ChatRole.System, result[0].Role);
    }

    [Fact]
    public async Task Degenerate_orphan_tool_at_head_is_advanced_past_not_kept()
    {
        // Defense in depth: history starts (post-system) with Tool-role
        // orphans whose matching Assistant tool_calls do not exist. This
        // shouldn't happen in a well-formed session, but if it does via
        // recovery from broken state, the reducer must not emit a kept
        // window that starts with an orphan Tool — downstream providers
        // would reject the request.
        var reducer = new ExtractiveSessionReducer(keepRecentMessages: 2);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.Tool, [new FunctionResultContent("orphan-1", "r1")]),
            new(ChatRole.Tool, [new FunctionResultContent("orphan-2", "r2")]),
            new(ChatRole.Assistant, "Recovery assistant"),
            new(ChatRole.Assistant, "More assistant")
        };

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        // The kept window must not start with a Tool orphan. Leading Tool
        // messages are skipped, even if that shrinks the window below the
        // requested keepCount.
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.DoesNotContain(result.Skip(1), m => m.Role == ChatRole.Tool);
    }
}
