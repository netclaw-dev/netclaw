// -----------------------------------------------------------------------
// <copyright file="ToolApprovalEntryComparer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Platform-correct comparison rules for entries stored in
/// <c>tool-approvals.json</c>. Approval entries embed both filesystem paths
/// (case-sensitive on POSIX, case-insensitive on Windows) and verb tokens that
/// resolve to executables via the host's <c>$PATH</c> lookup, which honors
/// filesystem case rules. Folding case unconditionally on POSIX would let an
/// attacker who plants <c>Git</c> earlier in <c>$PATH</c> inherit the approval
/// the user issued for <c>git</c> — similarly for case-distinct directory pairs
/// like <c>/data/</c> vs <c>/Data/</c>.
/// </summary>
public static class ToolApprovalEntryComparer
{
    /// <summary>
    /// The <see cref="StringComparison"/> mode used for both the daemon's
    /// approval gate and for operator-driven CLI removal. Centralized here so
    /// every consumer of the approval file uses the same rule.
    /// </summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The <see cref="StringComparer"/> equivalent of <see cref="Comparison"/>,
    /// suitable for collection lookups.
    /// </summary>
    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Equality predicate for raw strings (verbs or directories). Does NOT
    /// normalize trailing path separators; callers comparing directory paths
    /// should pass values through <see cref="NormalizeDirectory"/> first or use
    /// <see cref="Equals(ApprovalEntry, ApprovalEntry)"/> which normalizes the
    /// directory half automatically.
    /// </summary>
    public static bool Equals(string? left, string? right) =>
        string.Equals(left, right, Comparison);

    /// <summary>
    /// Equality predicate matching the daemon's approval matcher: two
    /// entries are equal when their verbs match and their normalized
    /// directories match (with both <c>null</c> directories considered equal —
    /// the global wildcard).
    /// </summary>
    public static bool Equals(ApprovalEntry left, ApprovalEntry right)
        => Equals(left.Verb, right.Verb)
           && Equals(NormalizeDirectory(left.Directory), NormalizeDirectory(right.Directory));

    /// <summary>
    /// Canonicalizes a directory path for storage and comparison. Trims
    /// surrounding whitespace, collapses null/empty/whitespace to <c>null</c>
    /// (the global-wildcard sentinel), and strips a trailing path separator so
    /// <c>/path/</c> and <c>/path</c> compare equal. Preserves filesystem
    /// roots (<c>/</c>, <c>C:\</c>) intact.
    /// </summary>
    public static string? NormalizeDirectory(string? directory)
    {
        if (directory is null)
            return null;

        var trimmed = directory.Trim();
        if (trimmed.Length == 0)
            return null;

        // Preserve POSIX filesystem root.
        if (trimmed == "/")
            return trimmed;

        // Preserve Windows drive roots like "C:\" — TrimEnd would otherwise
        // leave "C:" which means "the drive's current directory," a different
        // location.
        if (OperatingSystem.IsWindows()
            && trimmed.Length == 3
            && trimmed[1] == ':'
            && (trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return trimmed;
        }

        var stripped = trimmed.TrimEnd('/', '\\');
        return stripped.Length == 0 ? trimmed : stripped;
    }

    /// <summary>
    /// Returns <paramref name="entry"/> with its verb trimmed and its
    /// directory normalized via <see cref="NormalizeDirectory"/>. Used by
    /// the store at write time so the on-disk file never accumulates
    /// trailing-slash directory variants or whitespace-padded verbs of the
    /// same logical entry — both shapes would silently never match a real
    /// candidate at the gate.
    /// </summary>
    public static ApprovalEntry Normalize(ApprovalEntry entry)
    {
        var trimmedVerb = entry.Verb?.Trim() ?? string.Empty;
        var normalizedDir = NormalizeDirectory(entry.Directory);

        var verbChanged = !string.Equals(trimmedVerb, entry.Verb, StringComparison.Ordinal);
        var dirChanged = !string.Equals(normalizedDir, entry.Directory, StringComparison.Ordinal);

        if (!verbChanged && !dirChanged)
            return entry;

        return entry with { Verb = trimmedVerb, Directory = normalizedDir };
    }
}
