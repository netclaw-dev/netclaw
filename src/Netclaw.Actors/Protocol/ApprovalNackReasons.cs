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
    /// The approval decision could not be persisted.
    /// </summary>
    public const string PersistFailed = "approval_persist_failed";
}
