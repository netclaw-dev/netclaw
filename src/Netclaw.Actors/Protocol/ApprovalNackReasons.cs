// -----------------------------------------------------------------------
// <copyright file="ApprovalNackReasons.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Canonical nack reasons for tool approval responses. Centralizes the magic
/// strings used by <see cref="CommandNack"/> when a
/// <see cref="ToolInteractionResponse"/> or <see cref="ToolInteractionTextResponse"/>
/// cannot be honored. Every consumer — session actor, channel bindings,
/// HTTP callback endpoints — should reference these constants.
/// </summary>
public static class ApprovalNackReasons
{
    /// <summary>
    /// No pending approval exists and the session has no history of ever having
    /// requested approval. The channel cold-path matched the text as approval-like,
    /// but this is a false positive — the message should fall through to normal ingress.
    /// </summary>
    public const string NoHistory = "approval_no_history";

    /// <summary>
    /// No pending approval exists, but the session has a history of approval activity.
    /// The prompt has expired.
    /// </summary>
    public const string PromptExpired = "approval_prompt_expired";

    /// <summary>
    /// The prompt has a recorded answer. A later answer for the same call changes nothing.
    /// The prompt did not expire, so channels must not tell the operator that it expired.
    /// The session also posts an "already has an answer" notice.
    /// </summary>
    public const string AlreadyAnswered = "approval_already_answered";

    /// <summary>
    /// The responding sender is not authorized to approve this tool action.
    /// </summary>
    public const string WrongRequester = "approval_wrong_requester";

    /// <summary>
    /// The selected option key was not among the options offered in the original prompt.
    /// </summary>
    public const string OptionUnavailable = "approval_option_unavailable";

    /// <summary>
    /// The session withdrew the prompt with no answer, for example because its child task was cancelled. Nothing ran.
    /// The session also posts the withdrawal reason. The prompt did not expire.
    /// </summary>
    public const string Withdrawn = "approval_withdrawn";

    /// <summary>
    /// A text answer names no prompt, and the sender can answer two or more open prompts. The session answers none
    /// and tells the sender to use the buttons.
    /// </summary>
    public const string UseButtons = "approval_use_buttons";

    /// <summary>
    /// The approval decision could not be persisted.
    /// </summary>
    public const string PersistFailed = "approval_persist_failed";
}
