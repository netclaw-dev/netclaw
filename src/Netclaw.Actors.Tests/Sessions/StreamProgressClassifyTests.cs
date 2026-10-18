// -----------------------------------------------------------------------
// <copyright file="StreamProgressClassifyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// <see cref="StreamProgress.Classify"/> decides which updates restart the thinking cap.
/// Every reasoning part is thinking, also a part with empty text: a backend can send
/// <c>"reasoning_content": ""</c> as a chunk of its own, and a signature-only part has no
/// text. Such a part must never count as an answer.
/// </summary>
public sealed class StreamProgressClassifyTests
{
    [Fact]
    public void Empty_reasoning_part_is_thinking()
    {
        var update = new ChatResponseUpdate { Contents = [new TextReasoningContent("")] };

        Assert.Equal(StreamActivity.Thinking, StreamProgress.Classify(update));
        Assert.True(StreamProgress.IsSubstantive(update));
    }

    [Fact]
    public void Reasoning_part_with_text_is_thinking()
    {
        var update = new ChatResponseUpdate { Contents = [new TextReasoningContent("plan")] };

        Assert.Equal(StreamActivity.Thinking, StreamProgress.Classify(update));
    }

    [Fact]
    public void Empty_reasoning_part_next_to_answer_text_is_an_answer()
    {
        var update = new ChatResponseUpdate
        {
            Contents = [new TextReasoningContent(""), new TextContent("Done.")]
        };

        Assert.Equal(StreamActivity.Answer, StreamProgress.Classify(update));
    }

    [Fact]
    public void Usage_only_update_is_a_keepalive()
    {
        var update = new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails())] };

        Assert.Equal(StreamActivity.Keepalive, StreamProgress.Classify(update));
    }
}
