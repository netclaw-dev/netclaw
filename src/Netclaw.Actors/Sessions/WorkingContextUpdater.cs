// -----------------------------------------------------------------------
// <copyright file="WorkingContextUpdater.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Applies confirmed file activity from successful receipts and child runs.
/// Accepts project declarations only from successful directory-tool receipts.
/// </summary>
internal static class WorkingContextUpdater
{
    public static WorkingContext AcceptToolResult(
        WorkingContext current,
        SerializableChatMessage message,
        ToolInvocationReceipt? receipt)
    {
        var updated = UpdateFromToolReceipt(current, receipt);
        return message.Name is SetWorkingDirectoryTool.ToolName
            && receipt is ToolInvocationReceipt.Succeeded { DeclaredProjectDirectory: { } projectDir }
            ? updated.WithProjectDirectory(projectDir)
            : updated;
    }

    public static WorkingContext MergeSuccessfulSubAgentWorkingContext(
        WorkingContext current,
        ChildRunCompletion completion) => completion switch
        {
            ChildRunCompletion.Completed completed => MergeConfirmedChanges(current, completed.WorkingContext),
            ChildRunCompletion.Partial partial => MergeConfirmedChanges(current, partial.WorkingContext),
            ChildRunCompletion.Failed or ChildRunCompletion.Cancelled => current,
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        };

    private static WorkingContext MergeConfirmedChanges(WorkingContext current, WorkingContextDelta child)
    {
        var updated = current;
        foreach (var path in child.ConfirmedChangedFiles)
            updated = updated.AddRecentFile(path);

        return updated;
    }

    public static WorkingContext UpdateFromToolReceipts(
        WorkingContext current,
        IReadOnlyList<SerializableChatMessage> orderedResults,
        IReadOnlyDictionary<string, ToolInvocationReceipt> receipts)
    {
        var updated = current;
        foreach (var result in orderedResults)
        {
            if (result.ToolCallId is not { } callId
                || !receipts.TryGetValue(callId.Value, out var receipt)
                || receipt is not ToolInvocationReceipt.Succeeded success)
            {
                continue;
            }

            foreach (var activity in success.FileActivity)
                updated = updated.AddRecentFile(activity.CanonicalPath);
        }

        return updated;
    }

    public static WorkingContext UpdateFromToolReceipt(
        WorkingContext current,
        ToolInvocationReceipt? receipt)
    {
        if (receipt is not ToolInvocationReceipt.Succeeded success)
            return current;

        var updated = current;
        foreach (var activity in success.FileActivity)
            updated = updated.AddRecentFile(activity.CanonicalPath);
        return updated;
    }
}
