// -----------------------------------------------------------------------
// <copyright file="BashDirectoryScopeProjection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

/// <summary>
/// One command occurrence analyzed again in one directory where it can execute.
/// </summary>
/// <param name="IntentDirectory">
/// The directory of the causal list item that this occurrence reads after, or null.
/// </param>
internal sealed record ScopedShellApprovalSlice(
    ShellCommandAnalysis Analysis,
    ShellApprovalAnalysis Approval,
    string WorkingDirectory,
    string? IntentDirectory);

/// <summary>
/// Gives each Bash command occurrence every directory where it can execute.
/// </summary>
/// <remarks>
/// The parser proves the reachable directories from its <c>WorkingDirectoryEffect</c> facts.
/// Each slice creates ordinary candidates in its own directory, so a stored grant
/// applies only to the directory where the occurrence runs.
/// A causal list (<c>cd dir &amp;&amp; action; diagnostic</c>) also marks each diagnostic
/// with the directory that the list changed to. Only the reviewed-safe intent rule reads that mark.
/// </remarks>
internal sealed record BashDirectoryScopeProjection(
    IReadOnlyList<ApprovalCandidate> Candidates,
    IReadOnlyList<ScopedShellApprovalSlice> Slices)
{
    private const int MaximumCandidates = 256;

    /// <summary>True when the command is a causal list. Its candidates keep the causal coverage rules.</summary>
    internal bool IsCausalList => Slices.Any(static slice => slice.IntentDirectory is not null);

    /// <summary>Returns the slice that produced a candidate occurrence, or null.</summary>
    internal ScopedShellApprovalSlice? FindSlice(CommandOccurrence? occurrence)
        => occurrence is null
            ? null
            : Slices.FirstOrDefault(slice => ReferenceEquals(slice.Analysis.Commands[0], occurrence));

    internal static bool TryCreate(
        ShellCommandAnalysis source,
        ShellCommandPolicy commandPolicy,
        ShellApprovalMatcher matcher,
        [NotNullWhen(true)] out BashDirectoryScopeProjection? projection)
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
            || !CanonicalPath.TryCreate(
                source.WorkingDirectory,
                relativeBase: null,
                ShellPathStyle.Posix,
                out var initialDirectory)
            || !source.Environment.TryProjectFiniteBashScopes(
                source.Source,
                initialDirectory.Value,
                out var finite)
            || finite is null
            || source.Commands.Count != finite.Parsed.Commands.Count
            || !source.Commands.Zip(finite.Parsed.Commands).All(static pair =>
                HasSameAuthoredElements(pair.First.Clause, pair.Second.Clause)))
        {
            return false;
        }

        // A causal list keeps the link rule of the causal intent: the platform temporary
        // alias (R7), such as macOS /tmp -> /private/tmp, is a valid scope. Other lists keep
        // the volume root rule.
        var intents = FindCausalIntents(source, matcher);
        var hostLinks = intents.Count > 0
            ? LinkRule.FromVolumeRootExceptTemporaryAlias
            : LinkRule.FromVolumeRoot;

        // Reject a parser result that differs from the authored source or Netclaw analysis.
        var slices = new List<(int OccurrenceIndex, ScopedShellApprovalSlice Slice)>();
        var candidateCount = 0;
        foreach (var scoped in finite.Commands)
        {
            var occurrenceIndex = IndexOf(finite.Parsed.Commands, scoped.SourceOccurrence);
            if (occurrenceIndex < 0
                || scoped.SourceStart < 0
                || scoped.SourceStart > source.Source.Length
                || scoped.Source.Length > source.Source.Length - scoped.SourceStart
                || !source.Source.AsSpan(scoped.SourceStart, scoped.Source.Length)
                    .SequenceEqual(scoped.Source.AsSpan())
                || !CanonicalPath.TryCreate(
                    scoped.WorkingDirectory,
                    relativeBase: null,
                    ShellPathStyle.Posix,
                    out var scopedPath)
                || !string.Equals(scopedPath.Value, scoped.WorkingDirectory, StringComparison.Ordinal)
                || scoped.ScopedOccurrence.WorkingDirectory is not ShellValueDomain.Exact scopedDirectory
                || !string.Equals(scopedDirectory.Value, scopedPath.Value, StringComparison.Ordinal))
            {
                return false;
            }

            var directory = scopedPath.Value;
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
                analysis,
                hostLinks);
            candidateCount += approval.Candidates.Count;
            if (approval.IsMessy
                || approval.Candidates.Count == 0
                || candidateCount > MaximumCandidates)
            {
                return false;
            }

            slices.Add((occurrenceIndex, new ScopedShellApprovalSlice(analysis, approval, directory, IntentDirectory: null)));
        }

        var marked = slices
            .Select(item => intents.TryGetValue(item.OccurrenceIndex, out var intent)
                ? item.Slice with { IntentDirectory = intent }
                : item.Slice)
            .ToArray();
        projection = new BashDirectoryScopeProjection(
            Array.AsReadOnly(marked.SelectMany(static slice => slice.Approval.Candidates).ToArray()),
            Array.AsReadOnly(marked));
        return true;
    }

    /// <summary>
    /// Finds the diagnostics of a causal list: <c>cd dir &amp;&amp; action</c>, then only
    /// <c>;</c> statements that read below <c>dir</c>, optionally repeated.
    /// Returns an empty map when any list item has another shape.
    /// </summary>
    private static IReadOnlyDictionary<int, string> FindCausalIntents(
        ShellCommandAnalysis source,
        ShellApprovalMatcher matcher)
    {
        var none = new Dictionary<int, string>();
        var commands = source.Commands;
        if (commands.Count < 3 || !TryGetTopLevelList(commands, out var list))
            return none;

        var intents = new Dictionary<int, string>();
        string? intent = null;
        for (var index = 0; index < commands.Count; index++)
        {
            var occurrence = commands[index];
            var item = list.Items[index];
            if (TryGetExactAbsoluteTarget(occurrence, out var target))
            {
                var expectedOperator = index == 0 ? CompoundOperator.None : CompoundOperator.Sequence;
                if (item.Operator != expectedOperator
                    || index + 1 >= commands.Count
                    || list.Items[index + 1].Operator != CompoundOperator.AndIf
                    || commands[index + 1].WorkingDirectoryEffect is not ShellWorkingDirectoryEffect.Unchanged
                    || !IsPrerequisite(matcher, occurrence, source.WorkingDirectory)
                    || !IsPrerequisite(matcher, commands[index + 1], source.WorkingDirectory))
                {
                    return none;
                }

                intent = target;
                index++;
                continue;
            }

            if (intent is null
                || item.Operator != CompoundOperator.Sequence
                || occurrence.WorkingDirectoryEffect is not ShellWorkingDirectoryEffect.Unchanged
                || HasUnknownArgumentValue(occurrence)
                || ShellRedirectPolicyFacts.HasFileWritingRedirect(occurrence)
                || !StaysWithinIntent(matcher, occurrence, intent))
            {
                return none;
            }

            intents.Add(index, intent);
        }

        return intents;
    }

    // Safe policy alone cannot establish intent: the directory change and its action need real candidates.
    private static bool IsPrerequisite(
        ShellApprovalMatcher matcher,
        CommandOccurrence occurrence,
        string? workingDirectory)
        => matcher.ExtractCandidatesForOccurrence(
                occurrence,
                workingDirectory,
                resolveUnknownPathsFromEffectiveValues: false,
                LinkRule.FromVolumeRootExceptTemporaryAlias) is { Count: > 0 } candidates
           && !candidates.Any(ApprovalPatternMatching.IsPureSideEffect);

    private static bool StaysWithinIntent(
        ShellApprovalMatcher matcher,
        CommandOccurrence occurrence,
        string intentDirectory)
        => matcher.ExtractCandidatesForOccurrence(
                occurrence,
                intentDirectory,
                resolveUnknownPathsFromEffectiveValues: true,
                LinkRule.FromVolumeRootExceptTemporaryAlias) is { Count: > 0 } candidates
           && candidates.All(candidate =>
               candidate.Directory is null || IsWithinIntent(candidate.Directory, intentDirectory));

    private static bool TryGetTopLevelList(
        IReadOnlyList<CommandOccurrence> occurrences,
        [NotNullWhen(true)] out CommandListSyntax? list)
    {
        list = null;
        for (var index = 0; index < occurrences.Count; index++)
        {
            if (!TryGetListItem(occurrences[index], index, out var currentList)
                || occurrences[index].Ancestry[0].ChildIndex != 0
                || list is not null && !ReferenceEquals(list, currentList))
            {
                return false;
            }

            list = currentList;
        }

        return list is not null && list.Items.Count == occurrences.Count;
    }

    /// <summary>
    /// Returns the top-level Bash list when the occurrence is the plain simple
    /// command at list item <paramref name="index"/>. The causal list and the
    /// one-call directory advice read the same list shape.
    /// </summary>
    internal static bool TryGetListItem(
        CommandOccurrence occurrence,
        int index,
        [NotNullWhen(true)] out CommandListSyntax? list)
    {
        list = null;
        if (!occurrence.IsComplete
            || occurrence.ImmediateRole != CommandOccurrenceRole.Ordinary
            || occurrence.Ancestry.Count != 2
            || occurrence.Ancestry[0] is not { Ancestor: ShellBlockSyntax, Region: CommandAncestryRegion.Root }
            || occurrence.Ancestry[1] is not
            {
                Ancestor: CommandListSyntax current,
                Region: CommandAncestryRegion.Statement,
                ChildIndex: var childIndex
            }
            || childIndex != index
            || current.Items.Count <= index
            || current.Items[index] is not { Command: SimpleCommandSyntax simple } item
            || !ReferenceEquals(simple.Clause, occurrence.Clause)
            || !Enum.IsDefined(item.Operator))
        {
            return false;
        }

        list = current;
        return true;
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
            || exact.Value[0] != '/'
            || !CanonicalPath.TryCreate(exact.Value, relativeBase: null, ShellPathStyle.Posix, out var path))
        {
            return false;
        }

        target = path.Value;
        return true;
    }

    private static bool HasUnknownArgumentValue(CommandOccurrence occurrence) =>
        occurrence.Arguments.Any(static argument =>
            !argument.Argument.IsPath
            && argument.Value is ShellValueDomain.Unknown);

    // An intent directory is approval scope, not a trusted root. Its paths
    // must stay inside it without a link below it.
    private static bool IsWithinIntent(string path, string intentDirectory)
        => CanonicalPath.TryCreateHost(path, relativeBase: null, out var candidate)
           && CanonicalPath.TryCreateHost(intentDirectory, relativeBase: null, out var intent)
           && FileSystemAuthority.EvaluateMembership(
               candidate,
               [new PathBoundary.Folder(intent, LinkRule.BelowRoot)]) is PathDecision.Allowed;

    private static int IndexOf(IReadOnlyList<CommandOccurrence> occurrences, CommandOccurrence occurrence)
    {
        for (var index = 0; index < occurrences.Count; index++)
        {
            if (ReferenceEquals(occurrences[index], occurrence))
                return index;
        }

        return -1;
    }

    private static bool HasSameAuthoredElements(Clause first, Clause second)
        => first.Elements.Count == second.Elements.Count
           && first.Elements.Zip(second.Elements).All(static pair =>
               pair.First.Role == pair.Second.Role
               && string.Equals(pair.First.Raw, pair.Second.Raw, StringComparison.Ordinal));
}
