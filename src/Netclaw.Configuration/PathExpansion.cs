// -----------------------------------------------------------------------
// <copyright file="PathExpansion.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Single canonical home-token expander for configured path strings. Lives in
/// <c>Netclaw.Configuration</c> (the foundation project) so both Configuration
/// itself and downstream projects such as <c>Netclaw.Security</c> share one
/// implementation; previously each project carried its own copy with subtly
/// different behavior on Windows.
/// </summary>
public static class PathExpansion
{
    /// <summary>
    /// Expands shell-style home tokens in <paramref name="value"/>:
    /// <c>~</c> (alone or as a prefix), <c>$HOME</c>, <c>${HOME}</c>, and
    /// <c>%USERPROFILE%</c> (all case-insensitive). Returns <c>null</c> for
    /// <c>null</c> or whitespace-only input; otherwise returns the expanded
    /// path with surrounding whitespace trimmed.
    /// </summary>
    /// <remarks>
    /// When a token is actually replaced, the result has forward slashes
    /// rewritten to <see cref="Path.DirectorySeparatorChar"/> so output is
    /// consistent across the four token forms on Windows. Without this,
    /// <c>~/x</c> goes through <see cref="Path.Combine(string, string)"/> and
    /// emits a backslash, while <c>$HOME/x</c> goes through
    /// <see cref="string.Replace(string, string?)"/> and keeps the literal
    /// forward slash — producing visually inconsistent paths that break
    /// equality comparisons against <c>Path.Combine</c>-built expectations.
    /// Inputs that contain no tokens are returned verbatim (no separator
    /// rewriting), preserving the established "absolute path unchanged"
    /// contract used by security path callers.
    /// </remarks>
    public static string? ExpandHome(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return trimmed;

        var expanded = trimmed;

        if (expanded.StartsWith('~'))
        {
            expanded = expanded.Length == 1
                ? home
                : Path.Combine(home, expanded[1..].TrimStart('/', '\\'));
        }

        // Skip substring scans entirely when no env-var sigil is present — the
        // typical absolute-path candidate has neither (matches the perf
        // short-circuits introduced by #973).
        if (expanded.Contains('$', StringComparison.Ordinal))
        {
            expanded = expanded.Replace("$HOME", home, StringComparison.OrdinalIgnoreCase);
            expanded = expanded.Replace("${HOME}", home, StringComparison.OrdinalIgnoreCase);
        }

        if (expanded.Contains('%', StringComparison.Ordinal))
        {
            expanded = expanded.Replace("%USERPROFILE%", home, StringComparison.OrdinalIgnoreCase);
        }

        // Only canonicalize separators when an expansion actually happened.
        // string.Replace returns the same instance when no match is found, so
        // ReferenceEquals reliably detects the no-op case and lets us return
        // the original verbatim — which matters for callers that expect a
        // path like "/absolute/path" to round-trip unchanged on Windows.
        if (ReferenceEquals(expanded, trimmed))
            return trimmed;

        return expanded.Replace('/', Path.DirectorySeparatorChar);
    }
}
