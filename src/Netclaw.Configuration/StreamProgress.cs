// -----------------------------------------------------------------------
// <copyright file="StreamProgress.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Configuration;

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
    {
        if (update.FinishReason is not null)
            return true;

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                case FunctionCallContent:
                    return true;
                case UsageContent:
                    break;
                default:
                    return true;
            }
        }

        return false;
    }
}
