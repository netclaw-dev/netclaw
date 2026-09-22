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
/// The approval options and their scope semantics:
/// <list type="bullet">
/// <item><see cref="ApproveOnce"/> — run this one time only; persist nothing.</item>
/// <item><see cref="ApproveSession"/> — allow the extracted verbs in the prompt's
/// directory for the rest of the session, in session-scoped memory only.</item>
/// <item><see cref="ApproveAlways"/> — persist <c>(verb, prompt's directory)</c>
/// entries to <c>tool-approvals.json</c>. Folder-scoped grant.</item>
/// <item><see cref="ApproveRepository"/> — persist a grant for one Git repository.
/// Registered worktrees of that repository share the grant.</item>
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
    public const string ApproveRepository = "approve_repository";
    public const string ApproveEverywhere = "approve_everywhere";
    public const string ApproveAssignmentSessionV1 = "approve_assignment_session_v1";
    public const string ApproveAssignmentAlwaysV1 = "approve_assignment_always_v1";
    public const string ApproveAssignmentRepositoryV1 = "approve_assignment_repository_v1";
    public const string ApproveAssignmentEverywhereV1 = "approve_assignment_everywhere_v1";
    public const string Deny = "deny";

    public static ApprovalOptionKey ApproveOnceKey { get; } = new(ApproveOnce);
    public static ApprovalOptionKey ApproveSessionKey { get; } = new(ApproveSession);
    public static ApprovalOptionKey ApproveAlwaysKey { get; } = new(ApproveAlways);
    public static ApprovalOptionKey ApproveRepositoryKey { get; } = new(ApproveRepository);
    public static ApprovalOptionKey ApproveEverywhereKey { get; } = new(ApproveEverywhere);
    public static ApprovalOptionKey ApproveAssignmentSessionV1Key { get; } = new(ApproveAssignmentSessionV1);
    public static ApprovalOptionKey ApproveAssignmentAlwaysV1Key { get; } = new(ApproveAssignmentAlwaysV1);
    public static ApprovalOptionKey ApproveAssignmentRepositoryV1Key { get; } = new(ApproveAssignmentRepositoryV1);
    public static ApprovalOptionKey ApproveAssignmentEverywhereV1Key { get; } = new(ApproveAssignmentEverywhereV1);
    public static ApprovalOptionKey DenyKey { get; } = new(Deny);

    public const string ApproveOnceLabel = "Once";
    public const string ApproveSessionLabel = "This chat";
    public const string ApproveAlwaysLabel = "Always here";
    public const string ApproveRepositoryLabel = "This repository";
    public const string ApproveEverywhereLabel = "Always anywhere";
    public const string ApproveMcpToolLabel = "Always allow this tool";
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
        => CanonicalDecisionKey(optionKey) is ApproveEverywhere or Deny;

    /// <summary>
    /// Returns true when two option keys produce the same approval decision.
    /// Assignment variants remain unknown to older binaries and fail closed after rollback.
    /// </summary>
    public static bool HasSameDecision(string first, string second)
        => string.Equals(CanonicalDecisionKey(first), CanonicalDecisionKey(second), StringComparison.Ordinal);

    /// <summary>Returns true for the repository variants.</summary>
    public static bool IsRepository(string optionKey)
        => CanonicalDecisionKey(optionKey) == ApproveRepository;

    /// <summary>Returns true for a versioned assignment option.</summary>
    public static bool IsAssignmentVariant(string optionKey) => optionKey is
        ApproveAssignmentSessionV1
        or ApproveAssignmentAlwaysV1
        or ApproveAssignmentRepositoryV1
        or ApproveAssignmentEverywhereV1;

    /// <summary>
    /// Maps an approval option key to its short human-readable label. Returns
    /// the key unchanged when it is not a recognized option.
    /// </summary>
    public static string LabelFor(string optionKey) => LabelFor(optionKey, isMcpTool: false);

    /// <summary>
    /// Maps an approval option key to its context-aware label. MCP grants are
    /// tool-scoped rather than directory-scoped, so the global persistence key
    /// is rendered as "Always allow this tool" instead of "Always anywhere".
    /// </summary>
    public static string LabelFor(string optionKey, bool isMcpTool) => CanonicalDecisionKey(optionKey) switch
    {
        ApproveOnce => ApproveOnceLabel,
        ApproveSession => ApproveSessionLabel,
        ApproveAlways => ApproveAlwaysLabel,
        ApproveRepository => ApproveRepositoryLabel,
        ApproveEverywhere => isMcpTool ? ApproveMcpToolLabel : ApproveEverywhereLabel,
        Deny => DenyLabel,
        _ => optionKey
    };

    /// <summary>Maps an option variant to its approval decision key.</summary>
    public static string CanonicalDecisionKey(string optionKey) => optionKey switch
    {
        ApproveAssignmentSessionV1 => ApproveSession,
        ApproveAssignmentAlwaysV1 => ApproveAlways,
        ApproveAssignmentRepositoryV1 => ApproveRepository,
        ApproveAssignmentEverywhereV1 => ApproveEverywhere,
        _ => optionKey,
    };
}
