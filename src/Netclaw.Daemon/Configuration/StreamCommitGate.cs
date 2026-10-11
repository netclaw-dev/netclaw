// -----------------------------------------------------------------------
// <copyright file="StreamCommitGate.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Decides when a streaming attempt has started, for <see cref="RetryingChatClient"/> and
/// <see cref="RoutingChatClient"/>. Both may restart an attempt only until it has started.
/// </summary>
/// <remarks>
/// The OpenAI Responses adapter yields content-free updates for <c>response.created</c>,
/// <c>response.in_progress</c>, and <c>output_item.added</c> right after the headers. Treating
/// those as the first chunk made pre-first-chunk retry and failover almost unreachable on
/// Responses-based providers (#2296). An attempt now starts at its first update with real
/// output (<see cref="StreamProgress.IsSubstantive"/>). Until then, updates carrying ids or
/// metadata are held so a failed attempt's <c>ResponseId</c>/<c>MessageId</c> never reach the
/// consumer; a bare update is emitted in their place so the session watchdog still sees
/// liveness. Pure keepalives pass through unchanged. One gate is used per attempt.
/// </remarks>
internal sealed class StreamCommitGate
{
    private readonly List<ChatResponseUpdate> _held = [];

    public bool Committed { get; private set; }

    public IReadOnlyList<ChatResponseUpdate> Accept(ChatResponseUpdate update)
    {
        if (Committed)
            return [update];

        if (StreamProgress.IsSubstantive(update))
        {
            Committed = true;
            _held.Add(update);
            var released = _held.ToArray();
            _held.Clear();
            return released;
        }

        if (IsPureKeepalive(update))
            return [update];

        _held.Add(update);
        // Preserve liveness without exposing ids from an attempt that can still fail over.
        return [new ChatResponseUpdate()];
    }

    /// <summary>
    /// The attempt ended without real output: release what was held so the consumer
    /// still sees the attempt's ids and metadata.
    /// </summary>
    public IReadOnlyList<ChatResponseUpdate> Complete()
    {
        Committed = true;
        var released = _held.ToArray();
        _held.Clear();
        return released;
    }

    /// <summary>
    /// A content-free update that carries nothing a consumer would fold into the response.
    /// <see cref="ChatResponseUpdate.Role"/> is ignored: self-hosted providers send role-only
    /// keepalives during prefill.
    /// </summary>
    private static bool IsPureKeepalive(ChatResponseUpdate update) =>
        update.Contents.Count == 0
        && update.FinishReason is null
        && update.AuthorName is null
        && update.ResponseId is null
        && update.MessageId is null
        && update.ConversationId is null
        && update.ModelId is null
        && update.CreatedAt is null
        && update.ContinuationToken is null
        && update.RawRepresentation is null
        && (update.AdditionalProperties is null || update.AdditionalProperties.Count == 0);
}
