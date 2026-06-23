// -----------------------------------------------------------------------
// <copyright file="SessionPhase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Sessions;

/// <summary>
/// Explicit lifecycle phases for the session actor's state machine.
/// Transitions are validated by <see cref="LlmSessionActor.TransitionTo"/>.
/// </summary>
public enum SessionPhase
{
    /// <summary>During journal replay and snapshot recovery.</summary>
    Recovering,

    /// <summary>Accepts user messages, idle timeout active.</summary>
    Ready,

    /// <summary>LLM call or tool execution in flight, incoming messages buffered.</summary>
    Processing,

    /// <summary>Context compaction running, incoming messages buffered.</summary>
    Compacting,

    /// <summary>Draining: final memory distillation, then snapshot and stop.</summary>
    Passivating
}

internal static class SessionPhaseTransitions
{
    public static bool IsLegal(SessionPhase from, SessionPhase to) => from switch
    {
        SessionPhase.Recovering => to == SessionPhase.Ready,
        SessionPhase.Ready => to is SessionPhase.Processing or SessionPhase.Compacting or SessionPhase.Passivating,
        SessionPhase.Processing => to is SessionPhase.Ready or SessionPhase.Compacting,
        SessionPhase.Compacting => to is SessionPhase.Ready or SessionPhase.Processing,
        SessionPhase.Passivating => to is SessionPhase.Ready,
        _ => false
    };
}
