// -----------------------------------------------------------------------
// <copyright file="ApprovalEntry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// One persisted tool-approval grant: a verb chain paired with a directory
/// scope. <see cref="Directory"/> is null for the global wildcard
/// ("approve this verb in any directory"); otherwise it is an absolute path
/// and the entry only matches invocations whose cwd is under that path.
///
/// This record replaces the v1 flat string list in
/// <c>tool-approvals.json</c>. v1 entries (verbs, normalized commands,
/// directory roots, and bash fragments mingled in one list) are not
/// migrated — see <see cref="ToolApprovalStore.Load"/>.
/// </summary>
/// <remarks>
/// Record-synthesized <c>Equals</c>/<c>GetHashCode</c> use default string
/// equality (case-sensitive Ordinal) which diverges from the canonical
/// approval comparison on Windows (OrdinalIgnoreCase) and does not normalize
/// trailing path separators. Use
/// <see cref="ToolApprovalEntryComparer.Equals(ApprovalEntry, ApprovalEntry)"/>
/// when comparing entries for approval-store semantics; do not rely on the
/// record's built-in equality (e.g. <c>HashSet&lt;ApprovalEntry&gt;</c>,
/// <c>Enumerable.Distinct()</c>) for that purpose.
/// </remarks>
public sealed record ApprovalEntry
{
    /// <summary>
    /// The verb chain (e.g. <c>git remote</c>, <c>freshdesk</c>). For
    /// <c>shell_execute</c> this is the prefix of non-flag tokens extracted
    /// from a command; for other tools it is the tool name.
    /// </summary>
    [JsonPropertyName("verb")]
    public required string Verb { get; init; }

    /// <summary>
    /// Absolute directory path the grant is scoped to, or <c>null</c> for
    /// the global wildcard. Trailing slashes are normalized away by the
    /// matcher so <c>/path/</c> and <c>/path</c> compare equal.
    /// </summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; init; }

    /// <summary>
    /// When this grant was first persisted, or <c>null</c> for entries
    /// written before approval timestamps were tracked. Stamped by
    /// <see cref="ToolApprovalStore.AddApproval"/> at write time. This is an
    /// additive, optional field — its presence does not change the on-disk
    /// schema version. It is provenance only: it does NOT participate in
    /// approval matching or in
    /// <see cref="ToolApprovalEntryComparer.Equals(ApprovalEntry, ApprovalEntry)"/>,
    /// so re-granting an existing approval keeps the original timestamp.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// The user-visible scope label emitted by <c>netclaw approvals list</c>
    /// and shown in the TUI. Folder-scoped entries render as
    /// <c>&lt;verb&gt; in &lt;dir&gt;</c>; global wildcards render as
    /// <c>&lt;verb&gt; anywhere</c>. Round-trips with
    /// <see cref="TryParseScope"/>.
    /// </summary>
    public string FormatScope()
        => Directory is null ? $"{Verb} anywhere" : $"{Verb} in {Directory}";

    /// <summary>
    /// Inverse of <see cref="FormatScope"/>. Parses one of the two
    /// user-visible forms — <c>&lt;verb&gt; in &lt;directory&gt;</c> or
    /// <c>&lt;verb&gt; anywhere</c> — into a typed <see cref="ApprovalEntry"/>.
    /// Returns false with a non-empty <paramref name="error"/> for any other
    /// shape so callers can surface the parse failure as a user error rather
    /// than a silent best-effort match.
    /// </summary>
    public static bool TryParseScope(string input, [NotNullWhen(true)] out ApprovalEntry? entry, out string error)
    {
        entry = null;
        error = string.Empty;

        const string AnywhereSuffix = " anywhere";
        const string InSeparator = " in ";

        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            error = "Approval scope must not be empty.";
            return false;
        }

        if (trimmed.EndsWith(AnywhereSuffix, StringComparison.Ordinal))
        {
            var verb = trimmed[..^AnywhereSuffix.Length].TrimEnd();
            if (verb.Length == 0)
            {
                error = "'<verb> anywhere' must include a verb.";
                return false;
            }
            entry = new ApprovalEntry { Verb = verb, Directory = null };
            return true;
        }

        // First " in " separates verb from directory. Verb chains never
        // contain " in " as a literal token, so this split is unambiguous
        // for legitimate inputs.
        var inIndex = trimmed.IndexOf(InSeparator, StringComparison.Ordinal);
        if (inIndex > 0)
        {
            var verb = trimmed[..inIndex].TrimEnd();
            var directory = trimmed[(inIndex + InSeparator.Length)..].TrimStart();
            if (verb.Length == 0 || directory.Length == 0)
            {
                error = "'<verb> in <directory>' must include both verb and directory.";
                return false;
            }
            entry = new ApprovalEntry { Verb = verb, Directory = directory };
            return true;
        }

        error = $"Could not parse approval scope '{input}'.";
        return false;
    }
}
