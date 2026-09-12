// -----------------------------------------------------------------------
// <copyright file="BashCausalApprovalIntent.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

internal sealed record BashCausalApprovalCandidate(
    ApprovalCandidate Candidate,
    CommandOccurrence SourceOccurrence,
    ShellPolicyCandidateRole Role,
    string? IntentDirectory,
    IReadOnlyList<string> FallbackDirectories,
    IReadOnlyList<int> PrerequisiteIndexes);

internal static class BashCausalApprovalIntent
{
    internal static bool TryProject(
        ShellExecutionEnvironment environment,
        ShellCommandAnalysis execution,
        ShellApprovalMatcher matcher,
        Func<string, bool> isAllowedHostPath,
        out IReadOnlyList<BashCausalApprovalCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(isAllowedHostPath);
        candidates = [];
        if (environment.Grammar != ShellGrammar.Bash
            || !execution.IsResolved
            || execution.Commands.Count < 3
            || !TryGetTopLevelList(execution.Commands, out var list))
        {
            return false;
        }

        var projected = new List<BashCausalApprovalCandidate>();
        var prerequisites = new List<int>();
        if (!ShellPathRules.TryNormalize(
                execution.WorkingDirectory,
                ShellPathStyle.Posix,
                out var initialDirectory))
        {
            return false;
        }

        var fallbackDirectories = new List<string> { initialDirectory };
        string? intentDirectory = null;
        var hasConsumer = false;

        for (var index = 0; index < execution.Commands.Count; index++)
        {
            var occurrence = execution.Commands[index];
            var item = list.Items[index];
            if (TryGetExactAbsoluteTarget(occurrence, out var target))
            {
                var expectedOperator = index == 0
                    ? CompoundOperator.None
                    : CompoundOperator.Sequence;
                if (item.Operator != expectedOperator
                    || index + 1 >= execution.Commands.Count
                    || list.Items[index + 1].Operator != CompoundOperator.AndIf
                    || !TryGetPrerequisiteCandidates(
                        matcher,
                        occurrence,
                        execution.WorkingDirectory,
                        isAllowedHostPath,
                        out var transitionCandidates))
                {
                    return false;
                }

                var firstAction = execution.Commands[++index];
                if (firstAction.WorkingDirectoryEffect is not
                        ShellWorkingDirectoryEffect.Unchanged
                    || !TryGetPrerequisiteCandidates(
                        matcher,
                        firstAction,
                        execution.WorkingDirectory,
                        isAllowedHostPath,
                        out var actionCandidates))
                {
                    return false;
                }

                prerequisites.Clear();
                AppendPrerequisites(transitionCandidates, occurrence, projected, prerequisites);
                AppendPrerequisites(actionCandidates, firstAction, projected, prerequisites);
                if (intentDirectory is not null
                    && !fallbackDirectories.Contains(intentDirectory, StringComparer.Ordinal))
                {
                    fallbackDirectories.Add(intentDirectory);
                }

                intentDirectory = target;
                continue;
            }

            if (intentDirectory is null
                || item.Operator != CompoundOperator.Sequence
                || occurrence.WorkingDirectoryEffect is not
                    ShellWorkingDirectoryEffect.Unchanged
                || HasUnknownArgumentValue(occurrence)
                || ShellRedirectPolicyFacts.HasFileWritingRedirect(occurrence))
            {
                return false;
            }

            var intentCandidates = matcher.ExtractCandidatesForOccurrence(
                occurrence,
                intentDirectory,
                resolveUnknownPathsFromEffectiveValues: true,
                isAllowedHostPath);
            if (intentCandidates is not { Count: > 0 }
                || intentCandidates.Any(candidate =>
                    candidate.Directory is { } directory
                    && !IsWithinIntent(directory, intentDirectory)))
            {
                return false;
            }

            foreach (var candidate in intentCandidates)
            {
                projected.Add(new BashCausalApprovalCandidate(
                    candidate with { Directory = null, SourceOccurrence = null },
                    occurrence,
                    ShellPolicyCandidateRole.CausalIntentConsumer,
                    intentDirectory,
                    Array.AsReadOnly(fallbackDirectories.ToArray()),
                    Array.AsReadOnly(prerequisites.ToArray())));
            }

            hasConsumer = true;
        }

        if (!hasConsumer)
            return false;

        candidates = Array.AsReadOnly(projected.ToArray());
        return true;
    }

    private static bool TryGetTopLevelList(
        IReadOnlyList<CommandOccurrence> occurrences,
        out CommandListSyntax list)
    {
        list = null!;
        for (var index = 0; index < occurrences.Count; index++)
        {
            var occurrence = occurrences[index];
            if (!occurrence.IsComplete
                || occurrence.ImmediateRole != CommandOccurrenceRole.Ordinary
                || occurrence.Ancestry.Count != 2
                || occurrence.Ancestry[0] is not
                {
                    Ancestor: ShellBlockSyntax,
                    Region: CommandAncestryRegion.Root,
                    ChildIndex: 0
                }
                || occurrence.Ancestry[1] is not
                {
                    Ancestor: CommandListSyntax currentList,
                    Region: CommandAncestryRegion.Statement,
                    ChildIndex: var childIndex
                }
                || childIndex != index
                || index > 0 && !ReferenceEquals(list, currentList))
            {
                return false;
            }

            list = currentList;
        }

        if (list.Items.Count != occurrences.Count)
            return false;

        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.Items[index].Command is not SimpleCommandSyntax simple
                || !ReferenceEquals(simple.Clause, occurrences[index].Clause)
                || !Enum.IsDefined(list.Items[index].Operator))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetPrerequisiteCandidates(
        ShellApprovalMatcher matcher,
        CommandOccurrence occurrence,
        string? executionWorkingDirectory,
        Func<string, bool> isAllowedHostPath,
        out IReadOnlyList<ApprovalCandidate> candidates)
    {
        candidates = matcher.ExtractCandidatesForOccurrence(
            occurrence,
            executionWorkingDirectory,
            resolveUnknownPathsFromEffectiveValues: false,
            isAllowedHostPath) ?? [];
        return candidates.Count > 0
               && !candidates.Any(ApprovalPatternMatching.IsPureSideEffect);
    }

    private static void AppendPrerequisites(
        IReadOnlyList<ApprovalCandidate> candidates,
        CommandOccurrence occurrence,
        List<BashCausalApprovalCandidate> projected,
        List<int> prerequisites)
    {
        foreach (var candidate in candidates)
        {
            prerequisites.Add(projected.Count);
            projected.Add(new BashCausalApprovalCandidate(
                candidate with { SourceOccurrence = null },
                occurrence,
                ShellPolicyCandidateRole.CausalPrerequisite,
                IntentDirectory: null,
                FallbackDirectories: [],
                PrerequisiteIndexes: []));
        }

    }

    private static bool TryGetExactAbsoluteTarget(
        CommandOccurrence occurrence,
        out string target)
    {
        target = string.Empty;
        if (occurrence.WorkingDirectoryEffect is not
                ShellWorkingDirectoryEffect.ChangesOnSuccess
            {
                Target: ShellValueDomain.Exact exact
            }
            || string.IsNullOrWhiteSpace(exact.Value)
            || exact.Value[0] != '/')
        {
            return false;
        }

        return ShellPathRules.TryNormalize(
            exact.Value,
            ShellPathStyle.Posix,
            out target);
    }

    private static bool HasUnknownArgumentValue(CommandOccurrence occurrence) =>
        occurrence.Arguments.Any(static argument =>
            !argument.Argument.IsPath
            && argument.Value is ShellValueDomain.Unknown);

    private static bool IsWithinIntent(string path, string intentDirectory)
    {
        try
        {
            return PathUtility.IsWithinRoot(path, intentDirectory)
                   && !PathUtility.ContainsSymlinkSegment(intentDirectory, path);
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or IOException
                                      or NotSupportedException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            return false;
        }
    }
}
