// -----------------------------------------------------------------------
// <copyright file="SessionStateCompactionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Unit tests for <see cref="SessionState.ClearOldToolResults"/> and
/// compaction-related state transitions.
/// </summary>
public class SessionStateCompactionTests
{
    [Fact]
    public void ClearOldToolResults_no_tool_messages_returns_unchanged()
    {
        var state = SessionState.Empty
            .AddUserMessage("Hello")
            .AddErrorReply("Hi there"); // Using AddErrorReply to add an assistant message

        var (result, cleared) = state.ClearOldToolResults(3);

        Assert.Equal(0, cleared);
        Assert.Equal(state.History.Count, result.History.Count);
    }

    [Fact]
    public void ClearOldToolResults_fewer_than_keep_returns_unchanged()
    {
        var state = BuildStateWithToolResults(2);

        var (result, cleared) = state.ClearOldToolResults(3);

        Assert.Equal(0, cleared);
        Assert.Equal(state.History.Count, result.History.Count);
    }

    [Fact]
    public void ClearOldToolResults_clears_oldest_keeps_recent()
    {
        // 5 tool results, keep 2 → should clear 3
        var state = BuildStateWithToolResults(5);

        var (result, cleared) = state.ClearOldToolResults(2);

        Assert.Equal(3, cleared);

        // Verify the cleared messages have placeholder content
        var toolMessages = result.History
            .Where(m => m.Role == ChatRole.Tool)
            .ToList();
        Assert.Equal(5, toolMessages.Count);

        // First 3 should be cleared (placeholders)
        for (var i = 0; i < 3; i++)
        {
            Assert.StartsWith("[Tool result cleared", toolMessages[i].Content);
            Assert.NotNull(toolMessages[i].ToolCallId); // ToolCallId preserved
        }

        // Last 2 should be unchanged
        Assert.Equal("Tool result for call-3", toolMessages[3].Content);
        Assert.Equal("Tool result for call-4", toolMessages[4].Content);
    }

    [Fact]
    public void ClearOldToolResults_preserves_tool_call_id()
    {
        var state = BuildStateWithToolResults(3);

        var (result, cleared) = state.ClearOldToolResults(1);

        Assert.Equal(2, cleared);

        var clearedMsg = result.History.First(m => m.Role == ChatRole.Tool);
        Assert.Equal("call-0", clearedMsg.ToolCallId);
        Assert.Equal("web_search", clearedMsg.Name);
    }

    [Fact]
    public void ClearOldToolResults_keep_zero_clears_all()
    {
        var state = BuildStateWithToolResults(3);

        var (result, cleared) = state.ClearOldToolResults(0);

        Assert.Equal(3, cleared);
        Assert.All(
            result.History.Where(m => m.Role == ChatRole.Tool),
            m => Assert.StartsWith("[Tool result cleared", m.Content));
    }

    [Fact]
    public void ClearOldToolResults_negative_keep_treated_as_zero()
    {
        var state = BuildStateWithToolResults(2);

        var (result, cleared) = state.ClearOldToolResults(-1);

        Assert.Equal(2, cleared);
    }

    [Fact]
    public void Apply_SessionCompacted_preserves_system_prompt()
    {
        var state = (SessionState.Empty with
        {
            History = ImmutableList.Create(
                new SerializableChatMessage { Role = ChatRole.System, Content = "System prompt" })
        })
            .AddUserMessage("Hello")
            .AddErrorReply("Hi");

        var compacted = new SessionCompacted
        {
            SessionId = new SessionId("test"),
            Summary = "Summary of conversation",
            CompactedMessages =
            [
                new()
                {
                    Role = ChatRole.Assistant,
                    Content = "Summary of conversation"
                }
            ],
            TurnCountBefore = 1,
            CompactedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var result = state.Apply(compacted);

        // System prompt preserved at position 0
        Assert.Equal(ChatRole.System, result.History[0].Role);
        Assert.Equal("System prompt", result.History[0].Content);

        // Summary is position 1
        Assert.Equal(ChatRole.Assistant, result.History[1].Role);
        Assert.Equal("Summary of conversation", result.History[1].Content);

        // Only 2 messages total
        Assert.Equal(2, result.History.Count);
    }

    [Fact]
    public void Apply_SessionCompacted_without_system_prompt()
    {
        var state = SessionState.Empty
            .AddUserMessage("Hello")
            .AddErrorReply("Hi");

        var compacted = new SessionCompacted
        {
            SessionId = new SessionId("test"),
            Summary = "Summary",
            CompactedMessages =
            [
                new()
                {
                    Role = ChatRole.Assistant,
                    Content = "Summary"
                }
            ],
            TurnCountBefore = 1,
            CompactedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var result = state.Apply(compacted);

        // No system prompt, just the summary
        Assert.Single(result.History);
        Assert.Equal("Summary", result.History[0].Content);
    }

    /// <summary>
    /// Build a state with interleaved user/assistant/tool messages.
    /// Each "tool turn" is: user → assistant (with tool call) → tool result.
    /// </summary>
    private static SessionState BuildStateWithToolResults(int toolResultCount)
    {
        var state = SessionState.Empty;

        for (var i = 0; i < toolResultCount; i++)
        {
            state = state.AddUserMessage($"Question {i}");

            // Assistant with tool call
            state = state with
            {
                History = state.History.Add(new SerializableChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = string.Empty,
                    ToolCalls =
                    [
                        new SerializableToolCall
                        {
                            CallId = $"call-{i}",
                            Name = "web_search",
                            ArgumentsJson = $"{{\"query\":\"query {i}\"}}"
                        }
                    ]
                })
            };

            // Tool result
            state = state with
            {
                History = state.History.Add(new SerializableChatMessage
                {
                    Role = ChatRole.Tool,
                    Content = $"Tool result for call-{i}",
                    ToolCallId = $"call-{i}",
                    Name = "web_search"
                })
            };
        }

        return state;
    }
}
