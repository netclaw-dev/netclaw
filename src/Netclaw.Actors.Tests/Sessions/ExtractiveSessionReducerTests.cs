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
}
