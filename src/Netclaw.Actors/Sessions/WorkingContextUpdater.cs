// -----------------------------------------------------------------------
// <copyright file="WorkingContextUpdater.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Applies successful, canonical file activity produced by first-party tools.
/// Presentation strings and authored arguments never establish file activity.
/// </summary>
internal static class WorkingContextUpdater
{
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
                || receipt.Category != ToolInvocationOutcomeCategory.Success)
            {
                continue;
            }

            foreach (var activity in receipt.FileActivity)
                updated = updated.AddRecentFile(activity.CanonicalPath);
        }

        return updated;
    }

    public static WorkingContext UpdateFromToolReceipt(
        WorkingContext current,
        ToolInvocationReceipt? receipt)
    {
        if (receipt?.Category != ToolInvocationOutcomeCategory.Success)
            return current;

        var updated = current;
        foreach (var activity in receipt.FileActivity)
            updated = updated.AddRecentFile(activity.CanonicalPath);
        return updated;
    }
}
