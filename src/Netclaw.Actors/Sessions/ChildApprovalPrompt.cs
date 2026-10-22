// -----------------------------------------------------------------------
// <copyright file="ChildApprovalPrompt.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// The facts of a child consent prompt that the session and the channels share. The runtime makes the call ID of a
/// child prompt as <c>{runId}/subagent-approval/{n}</c> (<see cref="ParentSessionApprovalBridge"/>). No user-facing
/// text shows the call ID, because it holds the run ID (design D3, P3). A channel identifies a child prompt only by
/// <see cref="SessionProtocol.ToolInteractionLifetime.UntilClosed"/>, never by the call ID shape.
/// </summary>
public static class ChildApprovalPrompt
{
    private const string CallIdMarker = "/subagent-approval/";

    /// <summary>Makes the call ID of the prompt number <paramref name="number"/> of one child run.</summary>
    public static ToolCallId CreateCallId(string runId, long number) => new($"{runId}{CallIdMarker}{number}");

    /// <summary>The text of a withdrawn child prompt, for the closed redraw and for a late click (design D6).</summary>
    public static string WithdrawnText(WithdrawReason reason) => reason switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown withdraw reason.")
    };

    /// <summary>The redraw of a prompt that a person answered on another surface. It never says "Nothing ran".</summary>
    public const string AnsweredText = "This approval request has an answer.";

    /// <summary>
    /// The redraw of a prompt that the binding holds and that the session refused as withdrawn, expired, or answered
    /// (review fix B1). A typical case is a prompt that the binding restored from its journal. The binding does not know
    /// which close it was, so the text states only the close.
    /// </summary>
    public const string ClosedText = "This approval request is closed.";

    /// <summary>The redraw text of a closed prompt.</summary>
    public static string CloseText(ToolInteractionCloseReason reason) => reason switch
    {
        ToolInteractionCloseReason.Answered => AnsweredText,
        ToolInteractionCloseReason.Withdrawn withdrawn => WithdrawnText(withdrawn.Reason),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown close reason.")
    };

    /// <summary>
    /// The reply to a text answer when the sender can answer <paramref name="openCount"/> prompts (design D7). A text
    /// answer names no prompt, so with two or more open prompts it answers none.
    /// </summary>
    public static string UseButtonsText(int openCount)
        => $"{openCount} approval prompts are open. Use the buttons on the prompt you mean.";
}
