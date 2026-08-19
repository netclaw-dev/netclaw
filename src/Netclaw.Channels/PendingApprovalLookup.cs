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
/// Selects which candidate wins when more than one pending request matches.
/// The requester check is the same for both orders; only the tie-break differs.
/// </summary>
public enum ApprovalMatchOrder
{
    /// <summary>The most recent match wins. Discord and Mattermost use this.</summary>
    Newest,

    /// <summary>The earliest match wins. Slack uses this.</summary>
    Oldest
}

/// <summary>
/// Finds the pending approval a channel binding actor should act on. A
/// given call ID takes priority; otherwise the pending request the sender
/// is allowed to approve wins, picked by the channel's match order.
/// </summary>
public static class PendingApprovalLookup
{
    public static (ApprovalLookupResult Result, TRequest? Pending) Resolve<TRequest, TPromptId>(
        IReadOnlyList<TRequest> pendingRequests,
        string approvingSenderId,
        ToolCallId? callId,
        ApprovalMatchOrder matchOrder)
        where TRequest : PendingApprovalRequest<TPromptId>
        where TPromptId : struct
    {
        if (callId is { } resolvedCallId)
        {
            var byCallId = Select(pendingRequests, p => p.CallId == resolvedCallId, matchOrder);
            if (byCallId is null)
                return (ApprovalLookupResult.NotFound, null);
            if (!ApprovalButtonValueCodec.CanApprove(byCallId.RequesterPrincipal, byCallId.RequesterSenderId, approvingSenderId))
                return (ApprovalLookupResult.WrongRequester, null);
            return (ApprovalLookupResult.Matched, byCallId);
        }

        if (pendingRequests.Count == 0)
            return (ApprovalLookupResult.NotFound, null);

        var bySender = Select(
            pendingRequests,
            p => ApprovalButtonValueCodec.CanApprove(p.RequesterPrincipal, p.RequesterSenderId, approvingSenderId),
            matchOrder);
        return bySender is not null
            ? (ApprovalLookupResult.Matched, bySender)
            : (ApprovalLookupResult.WrongRequester, null);
    }

    private static TRequest? Select<TRequest>(
        IReadOnlyList<TRequest> pendingRequests,
        Func<TRequest, bool> predicate,
        ApprovalMatchOrder matchOrder)
        where TRequest : class
        => matchOrder is ApprovalMatchOrder.Newest
            ? pendingRequests.LastOrDefault(predicate)
            : pendingRequests.FirstOrDefault(predicate);
}
