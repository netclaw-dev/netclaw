// -----------------------------------------------------------------------
// <copyright file="TeamsProactiveDestinationResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Teams;

public enum TeamsDestinationResolutionDisposition
{
    Resolved,
    Unavailable,
    Ambiguous,
    Rejected
}

/// <summary>
/// A destination key is the canonical Teams session identity. It is scoped to
/// a single binding actor; callers cannot use it to select another tenant,
/// conversation kind, or channel root.
/// </summary>
public sealed record TeamsProactiveDestinationCandidate(
    SessionId SessionId,
    TeamsConversationScope Scope,
    long Generation,
    bool IsValid);

public sealed record TeamsDestinationResolution(
    TeamsDestinationResolutionDisposition Disposition,
    TeamsProactiveDestinationCandidate? Candidate = null,
    string? ReasonCode = null);

/// <summary>
/// Resolves current-session and explicitly named Teams destinations without
/// choosing an arbitrary candidate. The actor supplies only its authoritative
/// candidate set.
/// </summary>
public static class TeamsProactiveDestinationResolver
{
    public static TeamsDestinationResolution Resolve(
        SessionId requestingSession,
        TeamsConversationScope expectedScope,
        string? knownDestinationKey,
        IReadOnlyCollection<TeamsProactiveDestinationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var eligible = candidates
            .Where(candidate => candidate.IsValid
                && candidate.Generation > 0
                && candidate.Scope == expectedScope)
            .ToArray();

        if (knownDestinationKey is not null)
        {
            var explicitMatches = eligible.Where(candidate =>
                string.Equals(candidate.SessionId.Value, knownDestinationKey, StringComparison.Ordinal)).ToArray();
            return explicitMatches.Length switch
            {
                0 => new(TeamsDestinationResolutionDisposition.Unavailable, ReasonCode: "destination_unavailable"),
                1 when explicitMatches[0].SessionId == requestingSession => new(TeamsDestinationResolutionDisposition.Resolved, explicitMatches[0]),
                1 => new(TeamsDestinationResolutionDisposition.Rejected, ReasonCode: "destination_session_mismatch"),
                _ => new(TeamsDestinationResolutionDisposition.Ambiguous, ReasonCode: "destination_ambiguous")
            };
        }

        var current = eligible.Where(candidate => candidate.SessionId == requestingSession).ToArray();
        return current.Length switch
        {
            0 => new(TeamsDestinationResolutionDisposition.Unavailable, ReasonCode: "current_session_destination_missing"),
            1 => new(TeamsDestinationResolutionDisposition.Resolved, current[0]),
            _ => new(TeamsDestinationResolutionDisposition.Ambiguous, ReasonCode: "current_session_destination_ambiguous")
        };
    }
}
