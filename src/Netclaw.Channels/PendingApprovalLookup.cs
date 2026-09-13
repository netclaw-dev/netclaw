// -----------------------------------------------------------------------
// <copyright file="PendingApprovalLookup.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Channels;

public enum ApprovalLookupResult { Matched, WrongRequester, NotFound }

/// <summary>
/// Finds the pending approval a channel binding actor should act on. A
/// given call ID takes priority; otherwise the earliest pending request the
/// sender is allowed to approve wins.
/// </summary>
/// <remarks>
/// Slack, Discord, and Mattermost all resolve the earliest match. Discord and
/// Mattermost resolved the most recent match before the binding-actor
/// consolidation. The maintainer decided that one order applies to every
/// channel: the earliest pending approval wins. A user who answers a queue of
/// prompts answers them in the order that the channel shows them.
/// </remarks>
public static class PendingApprovalLookup
{
    public static (ApprovalLookupResult Result, TRequest? Pending) Resolve<TRequest, TPromptId>(
        IReadOnlyList<TRequest> pendingRequests,
        string approvingSenderId,
        ToolCallId? callId)
        where TRequest : PendingApprovalRequest<TPromptId>
        where TPromptId : struct
    {
        if (callId is { } resolvedCallId)
        {
            var byCallId = pendingRequests.FirstOrDefault(p => p.CallId == resolvedCallId);
            if (byCallId is null)
                return (ApprovalLookupResult.NotFound, null);
            if (!ApprovalButtonValueCodec.CanApprove(byCallId.RequesterPrincipal, byCallId.RequesterSenderId, approvingSenderId))
                return (ApprovalLookupResult.WrongRequester, null);
            return (ApprovalLookupResult.Matched, byCallId);
        }

        if (pendingRequests.Count == 0)
            return (ApprovalLookupResult.NotFound, null);

        var bySender = pendingRequests.FirstOrDefault(
            p => ApprovalButtonValueCodec.CanApprove(p.RequesterPrincipal, p.RequesterSenderId, approvingSenderId));
        return bySender is not null
            ? (ApprovalLookupResult.Matched, bySender)
            : (ApprovalLookupResult.WrongRequester, null);
    }
}
