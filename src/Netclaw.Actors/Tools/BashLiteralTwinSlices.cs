// -----------------------------------------------------------------------
// <copyright file="BashLiteralTwinSlices.cs" company="Petabridge, LLC">
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
/// The literal twins of each Bash command whose changeable words have a proved
/// finite set of values (ShellSyntaxTree 0.4.0-beta.23). Each twin is one slice:
/// the twin text analyzed in its own directory, with its normal candidates.
/// </summary>
/// <remarks>
/// <para>
/// Owner decision F1: Netclaw judges each twin as if the operator typed the
/// literal command. In
/// <c>for n in 8250 8244; do gh api repos/o/r/issues/$n; done</c>, the twins
/// <c>gh api repos/o/r/issues/8250</c> and <c>gh api repos/o/r/issues/8244</c>
/// get the screens and the candidates of the typed commands.
/// </para>
/// <para>
/// The strictest result wins. The caller screens each twin, so one denied twin
/// denies the call. The twin candidates replace the candidates of the source
/// command, so each twin candidate needs its own coverage. A command without
/// twins keeps its own candidates. The values of different words combine
/// independently, so some twins never run. That makes the check stricter.
/// </para>
/// <para>
/// SECURITY: a twin is evidence only. Bash runs the submitted source, never a
/// twin text. Read the structure and the assignments from the source command.
/// A twin that does not match its typed analysis, or that gives no candidates,
/// removes the twins of its source command, which then keeps its own candidates.
/// </para>
/// </remarks>
internal sealed record BashLiteralTwinSlices(IReadOnlyList<BashLiteralTwinSlices.TwinnedCommand> Commands)
{
    // The same limit as the directory proof. The parser gives at most 128 twins.
    private const int MaximumCandidates = 256;

    /// <summary>One source command and the slice of each of its twins.</summary>
    internal sealed record TwinnedCommand(
        CommandOccurrence Source,
        IReadOnlyList<ScopedShellApprovalSlice> Twins);

    /// <summary>Every twin slice, in source order.</summary>
    internal IEnumerable<ScopedShellApprovalSlice> Slices
        => Commands.SelectMany(static command => command.Twins);

    internal static bool TryCreate(
        ShellCommandAnalysis source,
        ShellCommandPolicy commandPolicy,
        ShellApprovalMatcher matcher,
        [NotNullWhen(true)] out BashLiteralTwinSlices? twins)
    {
        twins = null;
        if (source.Environment.Grammar != ShellGrammar.Bash
            || !source.IsResolved
            || source.RequiresExactTreeApproval
            || !source.Environment.TryProjectLiteralBashTwins(
                source.Source,
                source.WorkingDirectory,
                source.ManagedTemporary,
                out var projection)
            || projection is null
            // Netclaw expands a bundled wrapper (bash -lc) into more commands. The
            // twin indexes then name other commands, so such a call gets no twins.
            || projection.Parsed.Commands.Count != source.Commands.Count
            || !source.Commands.Zip(projection.Parsed.Commands).All(static pair =>
                BashDirectoryScopeProjection.HasSameAuthoredElements(pair.First.Clause, pair.Second.Clause)))
        {
            return false;
        }

        var commands = new List<TwinnedCommand>();
        var candidateCount = 0;
        foreach (var command in projection.Commands)
        {
            var index = command.SourceOccurrenceIndex;
            if (index < 0
                || index >= source.Commands.Count
                || !ReferenceEquals(projection.Parsed.Commands[index], command.SourceOccurrence))
            {
                return false;
            }

            var occurrence = source.Commands[index];
            if (!ShellCommandAnalysis.HasKnownStructure(occurrence)
                || !TryAnalyzeTwins(source, occurrence, command.Twins, commandPolicy, matcher, out var slices))
            {
                continue;
            }

            candidateCount += slices.Sum(static slice => slice.Approval.Candidates.Count);
            if (candidateCount > MaximumCandidates)
                return false;

            commands.Add(new TwinnedCommand(occurrence, slices));
        }

        if (commands.Count == 0)
            return false;

        twins = new BashLiteralTwinSlices(Array.AsReadOnly(commands.ToArray()));
        return true;
    }

    /// <summary>
    /// Replaces the candidates of each source command with the candidates of
    /// all of its twins. The other candidates do not change.
    /// </summary>
    /// <remarks>
    /// SECURITY: the union of the twin candidates is the strictest result. The
    /// call runs without a prompt only when each twin candidate is covered, and
    /// a prompt shows each uncovered twin candidate. An unresolved source keeps
    /// one exact answer, because it has no candidates to replace.
    /// </remarks>
    internal ShellApprovalAnalysis Apply(ShellApprovalAnalysis approval)
    {
        if (approval.IsMessy)
            return approval;

        var twinned = new Dictionary<CommandOccurrence, IReadOnlyList<ScopedShellApprovalSlice>>(
            ReferenceEqualityComparer.Instance);
        foreach (var command in Commands)
            twinned.Add(command.Source, command.Twins);

        var replaced = new HashSet<CommandOccurrence>(ReferenceEqualityComparer.Instance);
        var candidates = new List<ApprovalCandidate>();
        foreach (var candidate in approval.Candidates)
        {
            if (candidate.SourceOccurrence is { } occurrence
                && twinned.TryGetValue(occurrence, out var twins))
            {
                if (replaced.Add(occurrence))
                    candidates.AddRange(twins.SelectMany(static twin => twin.Approval.Candidates));

                continue;
            }

            candidates.Add(candidate);
        }

        // Each command of a resolved source has a candidate. A twinned command
        // without one means that the approval came from another analysis, and
        // its twins would get no check. The authorizer denies the call.
        if (replaced.Count != Commands.Count)
            throw new InvalidOperationException("A command with literal twins has no candidate in the approval.");

        return approval with { Candidates = Array.AsReadOnly(candidates.ToArray()) };
    }

    private static bool TryAnalyzeTwins(
        ShellCommandAnalysis source,
        CommandOccurrence occurrence,
        IReadOnlyList<BashLiteralTwin> twins,
        ShellCommandPolicy commandPolicy,
        ShellApprovalMatcher matcher,
        out IReadOnlyList<ScopedShellApprovalSlice> slices)
    {
        slices = [];
        if (twins.Count == 0)
            return false;

        var analyzed = new List<ScopedShellApprovalSlice>(twins.Count);
        foreach (var twin in twins)
        {
            if (!CanonicalPath.TryCreate(
                    twin.WorkingDirectory,
                    relativeBase: null,
                    ShellPathStyle.Posix,
                    out var path)
                || !string.Equals(path.Value, twin.WorkingDirectory, StringComparison.Ordinal)
                || twin.Occurrence.WorkingDirectory is not ShellValueDomain.Exact twinDirectory
                || !string.Equals(twinDirectory.Value, path.Value, StringComparison.Ordinal))
            {
                return false;
            }

            // The twin meets the same analysis as a typed command in its directory,
            // with the launch facts of this call. The facts must equal the twin facts.
            var directory = path.Value;
            var analysis = commandPolicy.Analyze(twin.Source, directory, source.ManagedTemporary);
            if (!analysis.IsResolved
                || analysis.Commands.Count != 1
                || !analysis.Commands[0].IsComplete
                || analysis.Commands[0].WorkingDirectory is not ShellValueDomain.Exact analyzedDirectory
                || !string.Equals(analyzedDirectory.Value, directory, StringComparison.Ordinal)
                || !BashDirectoryScopeProjection.HasSameAuthoredElements(
                    twin.Occurrence.Clause,
                    analysis.Commands[0].Clause)
                || !BashDirectoryScopeProjection.HasSameArgumentValues(twin.Occurrence, analysis.Commands[0]))
            {
                return false;
            }

            // A typed command with an unresolved part gets its exact candidate here too.
            var approval = ToolAccessPolicy.WithCommandCandidates(matcher.AnalyzeInvocation(
                new ToolName(ShellTool.ToolName),
                new Dictionary<string, object?>
                {
                    ["Command"] = twin.Source,
                    ["WorkingDirectory"] = directory
                },
                analysis,
                LinkRule.FromVolumeRoot));
            if (approval.IsMessy
                || approval.Candidates.Count == 0
                || !ShellApprovalMatcher.TryQualifyTwinCandidates(approval.Candidates, occurrence, out var qualified))
            {
                return false;
            }

            analyzed.Add(new ScopedShellApprovalSlice(
                analysis,
                approval with { Candidates = qualified },
                directory,
                IntentDirectory: null));
        }

        slices = Array.AsReadOnly(analyzed.ToArray());
        return true;
    }
}
