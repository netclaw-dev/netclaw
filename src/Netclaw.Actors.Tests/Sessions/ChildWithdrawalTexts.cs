// -----------------------------------------------------------------------
// <copyright file="ChildWithdrawalTexts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// The withdrawal texts of design D6 in the change <c>child-approval-events-and-parent-pause</c>. The redraw of a
/// closed prompt and the reply to a late click use the same text. No text names the child run or the agent.
/// The specs copy the table here on purpose: a change of the user-facing text must change a spec.
/// </summary>
internal static class ChildWithdrawalTexts
{
    public const string ButtonsOnlyGuard = "approval prompts are open. Use the buttons on the prompt you mean.";

    public static string For(WithdrawReason reason) => reason switch
    {
        WithdrawReason.CancelledByParent =>
            "This approval request was withdrawn because the task that asked for it was cancelled. Nothing ran.",
        WithdrawReason.ChildEnded =>
            "This approval request was withdrawn because the task that asked for it ended. Nothing ran.",
        WithdrawReason.ChildLost =>
            "This approval request was withdrawn because the task that asked for it stopped unexpectedly. Nothing ran.",
        WithdrawReason.DaemonRestarted =>
            "This approval request was withdrawn because Netclaw restarted before the task finished. Nothing ran.",
        WithdrawReason.PromptUnavailable =>
            "This approval request was withdrawn because Netclaw could not show it in this channel. Nothing ran.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    /// <summary>The reply when <paramref name="count"/> prompts are open for the sender of a text answer (design D7).</summary>
    public static string TextAnswerGuard(int count) => $"{count} {ButtonsOnlyGuard}";

    /// <summary>
    /// True for a session text that answers an approval response: an expired, answered, withdrawn, or guard reply.
    /// A spec fishes with this predicate and then compares the exact text, so a wrong reply fails fast with a diff.
    /// </summary>
    public static bool IsApprovalReply(string text)
        => text.Contains("approval request", StringComparison.OrdinalIgnoreCase)
           || text.Contains("approval prompt", StringComparison.OrdinalIgnoreCase);
}
