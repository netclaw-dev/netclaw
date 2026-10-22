// -----------------------------------------------------------------------
// <copyright file="PendingApprovalLookup.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels;

/// <summary>
/// The result of a pending-approval lookup. <see cref="Ambiguous"/>: a text answer names no prompt, and the sender can
/// answer two or more pending prompts, one of them a child prompt (design D7 of
/// <c>child-approval-events-and-parent-pause</c>).
/// </summary>
public enum ApprovalLookupResult { Matched, WrongRequester, NotFound, Ambiguous }

/// <summary>
/// Finds the pending approval a channel binding actor should act on. A
/// given call ID takes priority. A text answer has no call ID: it resolves the
/// earliest pending prompt that the sender can answer.
/// </summary>
/// <remarks>
/// Security boundary (design D7): a child prompt
/// (<see cref="ToolInteractionLifetime.UntilClosed"/>) does not name the child,
/// so model text can describe one command while a text answer approves another.
/// When a child prompt is among two or more prompts that the sender can answer,
/// the lookup is <see cref="ApprovalLookupResult.Ambiguous"/> and the channel
/// resolves none. With parent prompts only, the earliest prompt wins, so a
/// text-only channel can answer parallel parent calls.
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

        // <text-answer-lookup>
        var bySender = Answerable<TRequest, TPromptId>(pendingRequests, approvingSenderId);
        if (bySender.Count == 0)
            return (ApprovalLookupResult.WrongRequester, null);
        return bySender.Count > 1 && bySender.Any(static p => p.Lifetime == ToolInteractionLifetime.UntilClosed)
            ? (ApprovalLookupResult.Ambiguous, null)
            : (ApprovalLookupResult.Matched, bySender[0]);
        // </text-answer-lookup>
    }

    /// <summary>The pending prompts that <paramref name="approvingSenderId"/> can answer, in the order of the list.</summary>
    public static IReadOnlyList<TRequest> Answerable<TRequest, TPromptId>(
        IReadOnlyList<TRequest> pendingRequests,
        string approvingSenderId)
        where TRequest : PendingApprovalRequest<TPromptId>
        where TPromptId : struct
        => pendingRequests
            .Where(p => ApprovalButtonValueCodec.CanApprove(p.RequesterPrincipal, p.RequesterSenderId, approvingSenderId))
            .ToArray();
}
