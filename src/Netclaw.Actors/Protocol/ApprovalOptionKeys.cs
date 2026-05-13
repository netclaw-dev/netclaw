// -----------------------------------------------------------------------
// <copyright file="ApprovalOptionKeys.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Stable wire keys for tool approval options. These are part of the
/// channel/session protocol — channel adapters render them, the user picks one,
/// and the chosen key flows back to the session via
/// <see cref="ToolInteractionResponse.SelectedKey"/>. Renaming a key is a
/// breaking change to every channel adapter.
///
/// The five-button row and its scope semantics:
/// <list type="bullet">
/// <item><see cref="ApproveOnce"/> — run this one time only; persist nothing.</item>
/// <item><see cref="ApproveSession"/> — allow the extracted verbs in the prompt's
/// directory for the rest of the session, in session-scoped memory only.</item>
/// <item><see cref="ApproveAlways"/> — persist <c>(verb, prompt's directory)</c>
/// entries to <c>tool-approvals.json</c>. Folder-scoped grant.</item>
/// <item><see cref="ApproveEverywhere"/> — persist <c>(verb, null)</c> entries.
/// Global wildcard. Channel adapters render this as danger styling.</item>
/// <item><see cref="Deny"/> — refuse this call only; do NOT ban the verb for
/// future invocations. Channel adapters render this as danger styling.</item>
/// </list>
/// </summary>
public static class ApprovalOptionKeys
{
    public const string ApproveOnce = "approve_once";
    public const string ApproveSession = "approve_session";
    public const string ApproveAlways = "approve_always";
    public const string ApproveEverywhere = "approve_everywhere";
    public const string Deny = "deny";

    public const string ApproveOnceLabel = "Once";
    public const string ApproveSessionLabel = "This chat";
    public const string ApproveAlwaysLabel = "Always here";
    public const string ApproveEverywhereLabel = "Always anywhere";
    public const string DenyLabel = "Deny";

    /// <summary>
    /// The narrowest button-text cap across supported interactive channels
    /// (Slack <c>PlainText</c> = 76 chars, Discord button label = 80 chars).
    /// Approval option labels MUST stay within this bound for the channel
    /// adapter to render them; oversized labels cause Slack to reject the
    /// post with <c>invalid_blocks</c>, which then triggers an auto-deny.
    /// </summary>
    public const int MaxLabelLength = 76;

    /// <summary>
    /// Returns true when the option key represents a "danger"-styled action
    /// — global-wildcard persistence (<see cref="ApproveEverywhere"/>) and
    /// hard refusal (<see cref="Deny"/>) both warrant visual emphasis to
    /// reduce fat-finger risk.
    /// </summary>
    public static bool IsDangerStyled(string optionKey)
        => optionKey is ApproveEverywhere or Deny;
}
