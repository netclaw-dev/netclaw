// -----------------------------------------------------------------------
// <copyright file="ChildParkRulesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.SubAgents;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// PR 1 of durable-child-consent-wait (issue #2572), U3 and U4 of the cut plan. The two pure decisions of the park owner,
/// as tables. They are also the Stryker targets of child-resume-gate.
/// </summary>
public sealed class ChildParkRulesTests
{
    /// <summary>
    /// U3, design D3 "Refusals". The owner accepts a park only from the owned child of an Accepted run. A cancelling
    /// run ends as cancelled. A sender that the owner does not own gets the stop signal, and nothing is recorded.
    /// Planted bug: a park of a cancelling run shows a prompt, or a stranger can park a run.
    /// </summary>
    [Fact]
    public void Park_acceptance_follows_the_owner_and_the_lifecycle()
    {
        (bool Owned, ChildRunLifecycle Lifecycle, ChildParkDecision Expected)[] table =
        [
            (true, new ChildRunLifecycle.Accepted(null), ChildParkDecision.Accept),
            (true, new ChildRunLifecycle.Cancelling(null), ChildParkDecision.EndCancelled),
            (false, new ChildRunLifecycle.Accepted(null), ChildParkDecision.StopSender)
        ];

        Assert.Equal(table.Select(static row => row.Expected),
            table.Select(static row => ChildParkRules.AcceptPark(row.Owned, row.Lifecycle)));
    }

    /// <summary>
    /// U4, design D3 resume gate. A park waits while a request is open (guard 1) and while a restart drain is pending
    /// (guard 2). It resumes when every request has an answer. A run with no park does nothing.
    /// Planted bug: the first of two answers resumes the run, or an answer during a drain runs the call.
    /// </summary>
    [Fact]
    public void The_resume_gate_waits_for_every_answer_and_for_the_drain()
    {
        (bool HasPark, int Open, bool Drain, ChildResumeDecision Expected)[] table =
        [
            (true, 1, false, ChildResumeDecision.Wait),
            (true, 0, true, ChildResumeDecision.Wait),
            (true, 0, false, ChildResumeDecision.Resume),
            (false, 0, false, ChildResumeDecision.Nothing)
        ];

        Assert.Equal(table.Select(static row => row.Expected),
            table.Select(static row => ChildParkRules.ResumeGate(row.HasPark, row.Open, row.Drain)));
    }
}
