// -----------------------------------------------------------------------
// <copyright file="ShellPolicyPathFacts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

internal enum ShellPolicyPathOrigin
{
    EffectiveArgument = 0,
    AuthoredArgument = 1,
    AuthoredFileSystemValue = 2,
    Redirect = 3,
}

internal enum ShellPolicyPathResolutionState
{
    Known = 0,
    UnknownDynamic = 1,
    InvalidKnownValue = 2,
}

internal sealed record ShellPolicySourcePathFact(
    ShellPolicyPathOrigin Origin,
    ShellValueDomain Domain,
    ShellPathShape AuthoredPathShape)
{
    internal FileRedirectMode? RedirectMode { get; init; }

    internal bool RedirectIsComplete { get; init; } = true;
}

internal sealed record ShellPolicyResolvedPathFact(
    ShellPolicySourcePathFact Source,
    ShellPolicyPathResolutionState State,
    IReadOnlyList<CanonicalShellPath> Paths);

internal sealed record ShellPolicyScopePathFact(
    string? AuthoredValue,
    ShellPolicyPathResolutionState State,
    CanonicalShellPath? Path);

internal sealed record ShellPolicyResolvedPathView(
    ShellPolicyScopePathFact ResolutionBase,
    IReadOnlyList<ShellPolicyResolvedPathFact> Facts)
{
    internal bool HasUnprovedNonFileSystemSemantics { get; init; }
}

internal sealed record ShellPolicyCandidatePathFacts(
    ShellPolicyCandidateId CandidateId,
    CommandOccurrence? SourceOccurrence,
    ShellPolicyScopePathFact RealScope,
    ShellPolicyScopePathFact? IntentScope,
    IReadOnlyList<ShellPolicyScopePathFact> FallbackScopes,
    ShellPolicyResolvedPathView? Real,
    ShellPolicyResolvedPathView? Intent,
    IReadOnlyList<ShellPolicyResolvedPathView> Fallbacks);

internal static class ShellPolicyPathFacts
{
    internal static IReadOnlyList<ShellPolicyCandidatePathFacts> Create(
        IReadOnlyList<ShellPolicyCandidate> candidates,
        ShellPathStyle pathStyle)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var sourceCache = new Dictionary<CommandOccurrence, ShellPolicyOccurrencePathFacts>(
            ReferenceEqualityComparer.Instance);
        var projected = new ShellPolicyCandidatePathFacts[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.Id.Value != index)
                throw new InvalidOperationException("Shell candidate IDs must match path-fact order.");

            var sourceFacts = candidate.SourceOccurrence is { } occurrence
                ? GetOrCreateSourceFacts(sourceCache, occurrence)
                : null;
            var occurrenceDirectory = candidate.SourceOccurrence?.WorkingDirectory
                is ShellValueDomain.Exact exact
                ? exact.Value
                : null;
            var realScope = ResolveScope(
                candidate.Candidate.Directory ?? occurrenceDirectory,
                pathStyle);
            var realBase = ResolveScope(
                occurrenceDirectory ?? candidate.Candidate.Directory,
                pathStyle);
            var intentBase = candidate.IntentDirectory is null
                ? null
                : ResolveScope(
                    candidate.IntentDirectory,
                    pathStyle);
            var fallbackScopes = candidate.IntentFallbackDirectories
                .Select(path => ResolveScope(path, pathStyle))
                .ToArray();

            var real = sourceFacts?.Resolve(
                realBase,
                pathStyle,
                candidate.Candidate.Shell);
            var intent = sourceFacts is not null && intentBase is { } intentScope
                ? sourceFacts.Resolve(
                    intentScope,
                    pathStyle,
                    candidate.Candidate.Shell)
                : null;
            var fallbacks = sourceFacts is null
                ? []
                : fallbackScopes
                    .Select(path => sourceFacts.Resolve(
                        path,
                        pathStyle,
                        candidate.Candidate.Shell))
                    .ToArray();

            projected[index] = new ShellPolicyCandidatePathFacts(
                candidate.Id,
                candidate.SourceOccurrence,
                realScope,
                intentBase,
                Array.AsReadOnly(fallbackScopes),
                real,
                intent,
                Array.AsReadOnly(fallbacks));
        }

        return Array.AsReadOnly(projected);
    }

    internal static ShellPolicyScopePathFact ResolveScope(
        string? value,
        ShellPathStyle pathStyle)
    {
        CanonicalShellPath path = default;
        var state = string.IsNullOrWhiteSpace(value)
            ? ShellPolicyPathResolutionState.UnknownDynamic
            : ShellPathRules.TryNormalize(value, pathStyle, out var normalized)
              && CanonicalShellPath.TryCreate(normalized, pathStyle, out path)
                ? ShellPolicyPathResolutionState.Known
                : ShellPolicyPathResolutionState.InvalidKnownValue;
        return new ShellPolicyScopePathFact(
            value,
            state,
            state == ShellPolicyPathResolutionState.Known ? path : null);
    }

    private static ShellPolicyOccurrencePathFacts GetOrCreateSourceFacts(
        Dictionary<CommandOccurrence, ShellPolicyOccurrencePathFacts> cache,
        CommandOccurrence occurrence)
    {
        if (cache.TryGetValue(occurrence, out var facts))
            return facts;

        facts = ShellPolicyOccurrencePathFacts.Create(occurrence);
        cache.Add(occurrence, facts);
        return facts;
    }
}

internal sealed class ShellPolicyOccurrencePathFacts
{
    private readonly IReadOnlyList<ShellPolicySourcePathFact> _facts;
    private readonly bool _hasUnprovedNonFileSystemSemantics;
    private readonly bool _hasUnprovedBashGlobSemantics;

    private ShellPolicyOccurrencePathFacts(
        IReadOnlyList<ShellPolicySourcePathFact> facts,
        bool hasUnprovedNonFileSystemSemantics,
        bool hasUnprovedBashGlobSemantics)
    {
        _facts = facts;
        _hasUnprovedNonFileSystemSemantics = hasUnprovedNonFileSystemSemantics;
        _hasUnprovedBashGlobSemantics = hasUnprovedBashGlobSemantics;
    }

    internal static ShellPolicyOccurrencePathFacts Create(CommandOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        var facts = new List<ShellPolicySourcePathFact>();
        var hasUnprovedNonFileSystemSemantics = false;
        var hasUnprovedBashGlobSemantics = false;
        foreach (var argument in occurrence.Arguments)
        {
            var hasBoundedNonFileSystemValue =
                argument.AuthoredNonFileSystemValue is
                    ShellValueDomain.Exact or ShellValueDomain.FiniteSet;
            var hasBoundedFileSystemValue =
                argument.AuthoredFileSystemValue is
                    ShellValueDomain.Exact or ShellValueDomain.FiniteSet;
            if (argument.AuthoredNonFileSystemValue is not
                (ShellValueDomain.Unknown
                or ShellValueDomain.Exact
                or ShellValueDomain.FiniteSet)
                || (hasBoundedNonFileSystemValue && hasBoundedFileSystemValue))
            {
                hasUnprovedNonFileSystemSemantics = true;
            }

            if (!hasBoundedNonFileSystemValue
                && !argument.Argument.IsPath
                && argument.Argument.Kind == ArgKind.Glob)
            {
                hasUnprovedBashGlobSemantics = true;
            }

            if (!hasBoundedNonFileSystemValue && argument.Argument.IsPath)
            {
                facts.Add(CreateFact(
                    ShellPolicyPathOrigin.EffectiveArgument,
                    argument.Value,
                    ShellPathShape.Unknown));
            }

            if (!hasBoundedNonFileSystemValue
                && (argument.AuthoredPathShape != ShellPathShape.Unknown
                    || argument.AuthoredValue is ShellValueDomain.PathPattern))
            {
                facts.Add(CreateFact(
                    ShellPolicyPathOrigin.AuthoredArgument,
                    argument.AuthoredValue,
                    argument.AuthoredPathShape));
            }

            if (argument.AuthoredFileSystemValue is not ShellValueDomain.Unknown)
            {
                facts.Add(CreateFact(
                    ShellPolicyPathOrigin.AuthoredFileSystemValue,
                    argument.AuthoredFileSystemValue,
                    ShellPathShape.Unknown));
            }
        }

        foreach (var redirect in occurrence.Redirects.OfType<FileRedirectAnalysis>())
        {
            facts.Add(CreateFact(
                ShellPolicyPathOrigin.Redirect,
                redirect.Target,
                ShellPathShape.Unknown) with
            {
                RedirectMode = redirect.Mode,
                RedirectIsComplete = redirect.IsComplete
            });
        }

        return new ShellPolicyOccurrencePathFacts(
            Array.AsReadOnly(facts.ToArray()),
            hasUnprovedNonFileSystemSemantics,
            hasUnprovedBashGlobSemantics);
    }

    internal ShellPolicyResolvedPathView Resolve(
        ShellPolicyScopePathFact resolutionBase,
        ShellPathStyle pathStyle,
        ApprovalShell? shell)
    {
        var resolved = _facts
            .Select(fact => Resolve(fact, resolutionBase.AuthoredValue, pathStyle))
            .ToArray();
        return new ShellPolicyResolvedPathView(
            resolutionBase,
            Array.AsReadOnly(resolved))
        {
            HasUnprovedNonFileSystemSemantics =
                _hasUnprovedNonFileSystemSemantics
                || (shell == ApprovalShell.Bash && _hasUnprovedBashGlobSemantics)
        };
    }

    internal ShellPolicyResolvedPathView Resolve(
        string? resolutionBase,
        ShellPathStyle pathStyle,
        ApprovalShell? shell)
        => Resolve(
            ShellPolicyPathFacts.ResolveScope(
                resolutionBase,
                pathStyle),
            pathStyle,
            shell);

    private static ShellPolicySourcePathFact CreateFact(
        ShellPolicyPathOrigin origin,
        ShellValueDomain domain,
        ShellPathShape authoredPathShape)
        => new(origin, domain, authoredPathShape);

    private static ShellPolicyResolvedPathFact Resolve(
        ShellPolicySourcePathFact fact,
        string? resolutionBase,
        ShellPathStyle pathStyle)
    {
        IReadOnlyList<string>? values = fact.Domain switch
        {
            ShellValueDomain.Exact exact => [exact.Value],
            ShellValueDomain.FiniteSet finite => finite.Values,
            ShellValueDomain.PathPattern pattern => [pattern.CoveringDirectory],
            _ => null
        };
        if (values is null)
        {
            return new ShellPolicyResolvedPathFact(
                fact,
                ShellPolicyPathResolutionState.UnknownDynamic,
                []);
        }

        if (values.Count == 0)
        {
            return new ShellPolicyResolvedPathFact(
                fact,
                ShellPolicyPathResolutionState.InvalidKnownValue,
                []);
        }

        var paths = new CanonicalShellPath[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            if (!TryResolveCanonicalPath(
                    values[index],
                    resolutionBase,
                    pathStyle,
                    out paths[index]))
            {
                return new ShellPolicyResolvedPathFact(
                    fact,
                    ShellPolicyPathResolutionState.InvalidKnownValue,
                    []);
            }
        }

        return new ShellPolicyResolvedPathFact(
            fact,
            ShellPolicyPathResolutionState.Known,
            Array.AsReadOnly(paths));
    }

    internal static bool TryResolveCanonicalPath(
        string value,
        string? resolutionBase,
        ShellPathStyle pathStyle,
        out CanonicalShellPath path)
    {
        path = default;
        return ShellPathRules.TryResolve(
                   value,
                   resolutionBase,
                   pathStyle,
                   out var resolved)
               && CanonicalShellPath.TryCreate(resolved, pathStyle, out path);
    }
}
