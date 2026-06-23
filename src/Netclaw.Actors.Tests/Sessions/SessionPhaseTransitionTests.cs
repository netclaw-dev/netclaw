// -----------------------------------------------------------------------
// <copyright file="SessionPhaseTransitionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionPhaseTransitionTests
{
    [Theory]
    [InlineData(SessionPhase.Recovering, SessionPhase.Ready)]
    [InlineData(SessionPhase.Ready, SessionPhase.Processing)]
    [InlineData(SessionPhase.Ready, SessionPhase.Compacting)]
    [InlineData(SessionPhase.Ready, SessionPhase.Passivating)]
    [InlineData(SessionPhase.Processing, SessionPhase.Ready)]
    [InlineData(SessionPhase.Processing, SessionPhase.Compacting)]
    [InlineData(SessionPhase.Compacting, SessionPhase.Ready)]
    [InlineData(SessionPhase.Compacting, SessionPhase.Processing)]
    [InlineData(SessionPhase.Passivating, SessionPhase.Ready)]
    public void Legal_transitions_match_session_state_machine_spec(SessionPhase from, SessionPhase to)
    {
        Assert.True(SessionPhaseTransitions.IsLegal(from, to));
    }

    [Fact]
    public void Passivating_cannot_transition_directly_to_processing()
    {
        Assert.False(SessionPhaseTransitions.IsLegal(SessionPhase.Passivating, SessionPhase.Processing));
    }
}
