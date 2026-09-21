// -----------------------------------------------------------------------
// <copyright file="BashStaticCompoundApprovalProjection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

internal sealed record ScopedShellApprovalSlice(
    ShellCommandAnalysis Analysis,
    ShellApprovalAnalysis Approval,
    string WorkingDirectory);

internal sealed record BashStaticCompoundApprovalProjection(
    IReadOnlyList<ApprovalCandidate> Candidates,
    IReadOnlyList<ScopedShellApprovalSlice> Slices)
{
    private const int MaximumCandidates = 256;

    internal static bool TryCreate(
        ShellCommandAnalysis source,
        ShellCommandPolicy commandPolicy,
        ShellApprovalMatcher matcher,
        out BashStaticCompoundApprovalProjection? projection)
    {
        projection = null;
        if (source.Environment.Grammar != ShellGrammar.Bash
            || !source.IsResolved
            || source.RequiresExactTreeApproval
            || source.Commands.Count < 2
            || !source.Commands.Any(static command =>
                command.WorkingDirectoryEffect is ShellWorkingDirectoryEffect.ChangesOnSuccess
                {
                    Target: ShellValueDomain.Exact
                })
            || !ShellPathRules.TryNormalize(
                source.WorkingDirectory,
                ShellPathStyle.Posix,
                out var initialDirectory)
            || !source.Environment.TryProjectFiniteBashScopes(
                source.Source,
                initialDirectory,
                out var finite)
            || finite is null
            || source.Commands.Count != finite.Parsed.Commands.Count
            || !source.Commands.Zip(finite.Parsed.Commands).All(static pair =>
                HasSameAuthoredElements(pair.First.Clause, pair.Second.Clause)))
        {
            return false;
        }

        // Reject a parser result that differs from the authored source or Netclaw analysis.
        var candidates = new List<ApprovalCandidate>();
        var slices = new List<ScopedShellApprovalSlice>();
        foreach (var scoped in finite.Commands)
        {
            if (scoped.SourceStart < 0
                || scoped.SourceStart > source.Source.Length
                || scoped.Source.Length > source.Source.Length - scoped.SourceStart
                || !source.Source.AsSpan(scoped.SourceStart, scoped.Source.Length)
                    .SequenceEqual(scoped.Source.AsSpan())
                || !ShellPathRules.TryNormalize(
                    scoped.WorkingDirectory,
                    ShellPathStyle.Posix,
                    out var directory)
                || !string.Equals(directory, scoped.WorkingDirectory, StringComparison.Ordinal)
                || scoped.ScopedOccurrence.WorkingDirectory is not ShellValueDomain.Exact scopedDirectory
                || !string.Equals(scopedDirectory.Value, directory, StringComparison.Ordinal))
            {
                return false;
            }

            var analysis = commandPolicy.Analyze(scoped.Source, directory);
            if (!analysis.IsResolved
                || analysis.HasDynamicSyntax
                || analysis.RequiresExactTreeApproval
                || analysis.Commands.Count != 1
                || !analysis.Commands[0].IsComplete
                || analysis.Commands[0].WorkingDirectory is not ShellValueDomain.Exact analyzedDirectory
                || !string.Equals(analyzedDirectory.Value, directory, StringComparison.Ordinal)
                || !HasSameAuthoredElements(
                    scoped.ScopedOccurrence.Clause,
                    analysis.Commands[0].Clause))
            {
                return false;
            }

            var approval = matcher.AnalyzeInvocation(
                new ToolName(ShellTool.ToolName),
                new Dictionary<string, object?>
                {
                    ["Command"] = scoped.Source,
                    ["WorkingDirectory"] = directory
                },
                analysis);
            if (approval.IsMessy || approval.Candidates.Count == 0)
                return false;

            candidates.AddRange(approval.Candidates);
            if (candidates.Count > MaximumCandidates)
                return false;
            slices.Add(new ScopedShellApprovalSlice(analysis, approval, directory));
        }

        if (candidates.Count == 0)
            return false;

        projection = new BashStaticCompoundApprovalProjection(
            Array.AsReadOnly(candidates.ToArray()),
            Array.AsReadOnly(slices.ToArray()));
        return true;
    }

    private static bool HasSameAuthoredElements(Clause first, Clause second)
        => first.Elements.Count == second.Elements.Count
           && first.Elements.Zip(second.Elements).All(static pair =>
               pair.First.Role == pair.Second.Role
               && string.Equals(pair.First.Raw, pair.Second.Raw, StringComparison.Ordinal));
}
