// -----------------------------------------------------------------------
// <copyright file="SessionPhaseMachine.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Owns the session's explicit <see cref="SessionPhase"/> and enforces legal
/// transitions (per <see cref="SessionPhaseTransitions"/>). This is the metadata
/// + validation layer only — the actor still drives the matching <c>Become()</c>
/// behavior so phase tracking and behavior stay co-located there. Transient and
/// actor-owned; starts at <see cref="SessionPhase.Recovering"/>.
/// </summary>
internal sealed class SessionPhaseMachine
{
    public SessionPhase Current { get; private set; } = SessionPhase.Recovering;

    /// <summary>
    /// Attempts a validated transition to <paramref name="target"/>. On success,
    /// advances <see cref="Current"/> and reports the prior phase via
    /// <paramref name="from"/>; on an illegal transition returns <c>false</c> and
    /// leaves <see cref="Current"/> unchanged.
    /// </summary>
    public bool TryTransition(SessionPhase target, out SessionPhase from)
    {
        from = Current;
        if (!SessionPhaseTransitions.IsLegal(Current, target))
            return false;

        Current = target;
        return true;
    }
}
