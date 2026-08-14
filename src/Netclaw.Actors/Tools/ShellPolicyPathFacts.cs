// -----------------------------------------------------------------------
// <copyright file="ShellPolicyPathFacts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

internal enum ShellPolicyPathDomainKind
{
    Unknown = 0,
    Exact = 1,
    FiniteSet = 2,
    PathPattern = 3,
    Unsupported = 4,
}

internal enum ShellPolicyPathResolutionState
{
    Known = 0,
    UnknownDynamic = 1,
    InvalidKnownValue = 2,
}

internal enum ShellPolicyPathBaseKind
{
    Real = 0,
    Intent = 1,
    Fallback = 2,
}

internal sealed record ShellPolicySourcePathFact(
    ShellPolicyPathOrigin Origin,
    ShellPolicyPathDomainKind DomainKind,
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
    ShellPolicyPathBaseKind BaseKind,
    int BaseIndex,
    string? AuthoredValue,
    ShellPolicyPathResolutionState State,
    CanonicalShellPath? Path);

internal sealed record ShellPolicyResolvedPathView(
    ShellPolicyScopePathFact ResolutionBase,
    IReadOnlyList<ShellPolicyResolvedPathFact> Facts);

internal sealed record ShellPolicyCandidatePathFacts(
    ShellPolicyCandidateId CandidateId,
    CommandOccurrence? SourceOccurrence,
    ShellPolicyScopePathFact RealScope,
    ShellPolicyScopePathFact? IntentScope,
    IReadOnlyList<ShellPolicyScopePathFact> FallbackScopes,
    ShellPolicyResolvedPathView? Real,
    ShellPolicyResolvedPathView? Intent,
    IReadOnlyList<ShellPolicyResolvedPathView> Fallbacks);

internal sealed class ShellPolicyPathFacts
{
    private readonly ShellPolicyCandidatePathFacts[] _candidates;

    private ShellPolicyPathFacts(ShellPolicyCandidatePathFacts[] candidates)
    {
        _candidates = candidates;
        Candidates = Array.AsReadOnly(candidates);
    }

    internal IReadOnlyList<ShellPolicyCandidatePathFacts> Candidates { get; }

    internal ShellPolicyCandidatePathFacts For(ShellPolicyCandidateId candidateId)
    {
        var index = candidateId.Value;
        if ((uint)index >= (uint)_candidates.Length)
            throw new ArgumentOutOfRangeException(nameof(candidateId));

        return _candidates[index];
    }

    internal static ShellPolicyPathFacts Create(
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
            var realScope = ResolveRealScope(candidate, pathStyle);
            var realBase = ResolveRealBase(candidate, pathStyle);
            var intentBase = candidate.IntentDirectory is null
                ? null
                : ResolveScope(
                    ShellPolicyPathBaseKind.Intent,
                    baseIndex: 0,
                    candidate.IntentDirectory,
                    pathStyle);
            var fallbackScopes = candidate.IntentFallbackDirectories
                .Select((path, fallbackIndex) => ResolveScope(
                    ShellPolicyPathBaseKind.Fallback,
                    fallbackIndex,
                    path,
                    pathStyle))
                .ToArray();

            var real = sourceFacts?.Resolve(
                realBase,
                pathStyle);
            var intent = sourceFacts is not null && intentBase is { } intentScope
                ? sourceFacts.Resolve(
                    intentScope,
                    pathStyle)
                : null;
            var fallbacks = sourceFacts is null
                ? []
                : fallbackScopes
                    .Select(path => sourceFacts.Resolve(path, pathStyle))
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

        return new ShellPolicyPathFacts(projected);
    }

    private static ShellPolicyScopePathFact ResolveRealBase(
        ShellPolicyCandidate candidate,
        ShellPathStyle pathStyle)
    {
        var value = candidate.SourceOccurrence?.WorkingDirectory is ShellValueDomain.Exact exact
            ? exact.Value
            : candidate.Candidate.Directory;
        return ResolveScope(
            ShellPolicyPathBaseKind.Real,
            baseIndex: 0,
            value,
            pathStyle);
    }

    private static ShellPolicyScopePathFact ResolveRealScope(
        ShellPolicyCandidate candidate,
        ShellPathStyle pathStyle)
    {
        var value = candidate.Candidate.Directory
            ?? (candidate.SourceOccurrence?.WorkingDirectory is ShellValueDomain.Exact exact
                ? exact.Value
                : null);
        return ResolveScope(
            ShellPolicyPathBaseKind.Real,
            baseIndex: 0,
            value,
            pathStyle);
    }

    private static ShellPolicyScopePathFact ResolveScope(
        ShellPolicyPathBaseKind baseKind,
        int baseIndex,
        string? value,
        ShellPathStyle pathStyle)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ShellPolicyScopePathFact(
                baseKind,
                baseIndex,
                value,
                ShellPolicyPathResolutionState.UnknownDynamic,
                Path: null);
        }

        return ShellPathRules.TryNormalize(value, pathStyle, out var normalized)
               && CanonicalShellPath.TryCreate(normalized, pathStyle, out var path)
            ? new ShellPolicyScopePathFact(
                baseKind,
                baseIndex,
                value,
                ShellPolicyPathResolutionState.Known,
                path)
            : new ShellPolicyScopePathFact(
                baseKind,
                baseIndex,
                value,
                ShellPolicyPathResolutionState.InvalidKnownValue,
                Path: null);
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

    private ShellPolicyOccurrencePathFacts(IReadOnlyList<ShellPolicySourcePathFact> facts)
    {
        _facts = facts;
    }

    internal static ShellPolicyOccurrencePathFacts Create(CommandOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        var facts = new List<ShellPolicySourcePathFact>();
        foreach (var argument in occurrence.Arguments)
        {
            if (argument.Argument.IsPath)
            {
                facts.Add(CreateFact(
                    ShellPolicyPathOrigin.EffectiveArgument,
                    argument.Value,
                    ShellPathShape.Unknown));
            }

            if (argument.AuthoredPathShape != ShellPathShape.Unknown)
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

        return new ShellPolicyOccurrencePathFacts(Array.AsReadOnly(facts.ToArray()));
    }

    internal ShellPolicyResolvedPathView Resolve(
        ShellPolicyScopePathFact resolutionBase,
        ShellPathStyle pathStyle)
    {
        var resolved = _facts
            .Select(fact => Resolve(fact, resolutionBase.AuthoredValue, pathStyle))
            .ToArray();
        return new ShellPolicyResolvedPathView(
            resolutionBase,
            Array.AsReadOnly(resolved));
    }

    private static ShellPolicySourcePathFact CreateFact(
        ShellPolicyPathOrigin origin,
        ShellValueDomain domain,
        ShellPathShape authoredPathShape)
        => new(origin, ToDomainKind(domain), domain, authoredPathShape);

    private static ShellPolicyPathDomainKind ToDomainKind(ShellValueDomain domain)
        => domain switch
        {
            ShellValueDomain.Unknown => ShellPolicyPathDomainKind.Unknown,
            ShellValueDomain.Exact => ShellPolicyPathDomainKind.Exact,
            ShellValueDomain.FiniteSet => ShellPolicyPathDomainKind.FiniteSet,
            ShellValueDomain.PathPattern => ShellPolicyPathDomainKind.PathPattern,
            _ => ShellPolicyPathDomainKind.Unsupported
        };

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
