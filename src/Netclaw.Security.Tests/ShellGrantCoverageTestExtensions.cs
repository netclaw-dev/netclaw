// -----------------------------------------------------------------------
// <copyright file="ShellGrantCoverageTestExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Security.Tests;

/// <summary>
/// Test shorthand: extracts the shell candidates of an invocation and asks
/// whether stored grants cover each one. The production gate composes the same
/// two published operations (candidate extraction and grant matching).
/// </summary>
internal static class ShellGrantCoverageTestExtensions
{
    public static bool IsApproved(
        this ShellApprovalMatcher matcher,
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd)
    {
        // Empty candidates include a missing command, parser failures, and
        // dynamic syntax. None of them can be covered by a stored grant.
        var candidates = matcher.ExtractCandidates(toolName, arguments);
        return candidates.Count > 0
               && candidates.All(candidate =>
                   ApprovalPatternMatching.IsPureSideEffect(candidate)
                   || ApprovalPatternMatching.MatchesShellApproval(candidate, cwd, approvedEntries));
    }
}
