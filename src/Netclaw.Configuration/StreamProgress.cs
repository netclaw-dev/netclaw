// -----------------------------------------------------------------------
// <copyright file="StreamProgress.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Configuration;

/// <summary>
/// What a streaming update tells the watchdog about the model call.
/// </summary>
internal enum StreamActivity
{
    /// <summary>Content-free heartbeat or usage-only chunk. It proves the socket is alive.</summary>
    Keepalive,

    /// <summary>The update carries reasoning text and nothing else the user or a tool needs.</summary>
    Thinking,

    /// <summary>The update carries answer text, a tool call, a finish reason, or other non-usage content.</summary>
    Answer
}

/// <summary>
/// Classifies streaming updates by whether they carry real model output. Shared by the
/// session watchdog and the transport's retry/failover clients so both agree on when a
/// stream has actually started.
/// </summary>
internal static class StreamProgress
{
    /// <summary>
    /// True when an update represents real model progress. A finish reason, non-empty
    /// text/thinking, a tool call, or any non-usage content all count. Only a
    /// content-free heartbeat or a usage-only chunk (with no finish reason) is treated
    /// as a non-substantive keepalive. A provider that streams an error/refusal or other
    /// non-text content before the first token is still recognized as progress, not
    /// silently treated as a hang.
    /// </summary>
    internal static bool IsSubstantive(ChatResponseUpdate update)
        => Classify(update) != StreamActivity.Keepalive;

    /// <summary>
    /// Splits substantive progress into thinking and answer. Thinking is progress for
    /// the no-progress deadline, but only an answer update (text, tool call, finish
    /// reason, other content) resets the thinking cap.
    /// </summary>
    internal static StreamActivity Classify(ChatResponseUpdate update)
    {
        if (update.FinishReason is not null)
            return StreamActivity.Answer;

        var activity = StreamActivity.Keepalive;
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                case FunctionCallContent:
                    return StreamActivity.Answer;
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    activity = StreamActivity.Thinking;
                    break;
                case UsageContent:
                    break;
                default:
                    return StreamActivity.Answer;
            }
        }

        return activity;
    }
}
