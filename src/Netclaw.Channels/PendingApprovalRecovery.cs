// -----------------------------------------------------------------------
// <copyright file="PendingApprovalRecovery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Channels;

/// <summary>
/// Replays the journaled <see cref="PendingApprovalPromptTracked"/> and
/// <see cref="PendingApprovalPromptCleared"/> events into a channel binding
/// actor's in-memory pending-approval list during cold-spawn recovery.
/// </summary>
public static class PendingApprovalRecovery
{
    public static void ApplyTracked<TRequest, TPromptId>(
        List<TRequest> pendingRequests,
        PendingApprovalPromptTracked tracked,
        Func<string, TPromptId> wrapPromptId,
        Func<ToolCallId, string?, PrincipalClassification?, IReadOnlyList<string>, TPromptId?, string?, string?, TRequest> createRequest)
        where TRequest : PendingApprovalRequest<TPromptId>
        where TPromptId : struct
    {
        var promptId = wrapPromptId(tracked.PromptId);
        var existing = pendingRequests.LastOrDefault(p => p.CallId.Value == tracked.CallId);
        if (existing is not null)
        {
            existing.PromptId = promptId;
            return;
        }

        pendingRequests.Add(createRequest(
            new ToolCallId(tracked.CallId),
            tracked.RequesterSenderId,
            tracked.RequesterPrincipal,
            tracked.OptionKeys,
            promptId,
            tracked.ToolName,
            tracked.DisplayText));
    }

    public static void ApplyCleared<TRequest, TPromptId>(
        List<TRequest> pendingRequests,
        PendingApprovalPromptCleared cleared)
        where TRequest : PendingApprovalRequest<TPromptId>
        where TPromptId : struct
        => pendingRequests.RemoveAll(p => p.CallId.Value == cleared.CallId);
}
