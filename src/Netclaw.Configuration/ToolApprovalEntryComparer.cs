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
    /// Equality predicate matching the daemon's approval matcher.
    /// </summary>
    public static bool Equals(string? left, string? right) =>
        string.Equals(left, right, Comparison);
}
