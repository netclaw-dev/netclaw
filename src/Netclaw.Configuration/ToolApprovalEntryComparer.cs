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
    /// Compares shell-owned tokens or paths with that shell's path rules.
    /// </summary>
    public static bool Equals(string? left, string? right, ApprovalShell shell) =>
        string.Equals(
            left,
            right,
            shell == ApprovalShell.PowerShell
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    /// <summary>
    /// Equality predicate matching the daemon's approval matcher: two
    /// entries are equal when their verbs match and their normalized
    /// directories match (with both <c>null</c> directories considered equal —
    /// the global wildcard).
    /// </summary>
    public static bool Equals(ApprovalEntry left, ApprovalEntry right)
    {
        if (left.Shell != right.Shell || left.Match != right.Match)
        {
            return false;
        }

        var phraseMatches = left.Match == ApprovalMatchKind.TokenPrefix
            ? TokenSequencesEqual(left.VerbTokens, right.VerbTokens, left.Shell)
            : left.Shell is { } shell
                ? Equals(left.Verb, right.Verb, shell)
                : Equals(left.Verb, right.Verb);
        return phraseMatches &&
               (left.Shell is { } directoryShell
                   ? Equals(
                       NormalizeDirectory(left.Directory, directoryShell),
                       NormalizeDirectory(right.Directory, directoryShell),
                       directoryShell)
                   : Equals(
                       NormalizeDirectory(left.Directory),
                       NormalizeDirectory(right.Directory)));
    }

    /// <summary>
    /// Canonicalizes a directory path for comparison. It preserves significant
    /// whitespace and never maps a non-null value to the global sentinel. It
    /// strips redundant trailing separators and preserves file-system roots.
    /// </summary>
    public static string? NormalizeDirectory(string? directory)
        => NormalizeDirectory(directory, shell: null);

    /// <summary>
    /// Canonicalizes a directory with the selected shell path rules.
    /// </summary>
    public static string? NormalizeDirectory(string? directory, ApprovalShell? shell)
    {
        if (directory is null)
            return null;

        if (directory.Length == 0)
            return directory;

        if (shell == ApprovalShell.Bash)
        {
            return directory == "/"
                ? directory
                : directory.TrimEnd('/');
        }

        if (shell == ApprovalShell.PowerShell)
        {
            var windowsDirectory = directory.Replace('/', '\\');
            return windowsDirectory.Length == 3 &&
                   char.IsAsciiLetter(windowsDirectory[0]) &&
                   windowsDirectory[1] == ':' &&
                   windowsDirectory[2] == '\\'
                ? windowsDirectory
                : windowsDirectory.TrimEnd('\\');
        }

        var fullPath = Path.IsPathFullyQualified(directory)
            ? Path.GetFullPath(directory)
            : directory;
        var root = Path.IsPathFullyQualified(fullPath)
            ? Path.GetPathRoot(fullPath)
            : null;
        if (root is not null && Equals(fullPath, root))
            return fullPath;

        var stripped = fullPath.TrimEnd('/', '\\');
        return stripped.Length == 0 ? fullPath : stripped;
    }

    /// <summary>
    /// Returns <paramref name="entry"/> with its directory normalized via
    /// <see cref="NormalizeDirectory"/>. It preserves the exact verb text.
    /// </summary>
    public static ApprovalEntry Normalize(ApprovalEntry entry)
    {
        var normalizedDir = NormalizeDirectory(entry.Directory, entry.Shell);

        var dirChanged = !string.Equals(normalizedDir, entry.Directory, StringComparison.Ordinal);

        if (!dirChanged)
            return entry;

        return entry with { Directory = normalizedDir };
    }

    private static bool TokenSequencesEqual(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right,
        ApprovalShell? shell)
    {
        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var equal = shell is { } typedShell
                ? Equals(left[index], right[index], typedShell)
                : Equals(left[index], right[index]);
            if (!equal)
            {
                return false;
            }
        }

        return true;
    }
}
