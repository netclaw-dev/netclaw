// -----------------------------------------------------------------------
// <copyright file="OpenChildRunsMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// <see cref="SessionState.GetOpenChildRunIds"/> is the producer of <c>TurnCompleted.OpenChildRunIds</c>.
/// A headless run waits while the list is not empty. A wrong list gives one of two failures. A run that is
/// missing from the list lets the run exit early and lose the child result. A run that stays in the list for
/// ever makes the run wait for a turn that never comes.
/// <para>
/// The tests build the state directly, so each lifecycle, the quarantine rule, and the order get a decision
/// of their own. No test uses an actor, a timer, or a delay.
/// </para>
/// </summary>
public sealed class OpenChildRunsMutationTests
{
    private static readonly SessionId Owner = new("test/open-child-runs");

    [Fact]
    public void A_state_with_no_child_run_reports_none()
    {
        Assert.Empty(SessionState.Empty.GetOpenChildRunIds());
    }

    [Fact]
    public void Every_lifecycle_before_closed_is_open()
    {
        // A run is open until the parent records the follow-up turn: not at the terminal result, not at the
        // prepared result, and not when the session hands the result to the model (Consumed).
        var terminal = Terminal();
        var delivery = Delivery(terminal);
        var state = StateOf(
            Run("accepted", 1, new ChildRunLifecycle.Accepted(null)),
            Run("cancelling", 2, new ChildRunLifecycle.Cancelling(null)),
            Run("terminal", 3, new ChildRunLifecycle.Terminal(terminal)),
            Run("prepared", 4, new ChildRunLifecycle.Prepared(delivery)),
            Run("consumed", 5, new ChildRunLifecycle.Consumed(delivery)));

        Assert.Equal(
            ["accepted", "cancelling", "terminal", "prepared", "consumed"],
            Ids(state));
    }

    [Fact]
    public void A_closed_run_is_not_open()
    {
        // A run that stays in the list after its follow-up turn makes a headless run wait for ever.
        var state = StateOf(
            Run("closed", 1, Closed()),
            Run("accepted", 2, new ChildRunLifecycle.Accepted(null)));

        Assert.Equal(["accepted"], Ids(state));
    }

    [Fact]
    public void A_state_with_only_closed_runs_reports_none()
    {
        var state = StateOf(Run("closed-a", 1, Closed()), Run("closed-b", 2, Closed()));

        Assert.Empty(state.GetOpenChildRunIds());
    }

    [Fact]
    public void A_run_that_the_legacy_reader_quarantined_is_not_open()
    {
        // The session never starts a delivery turn for such a run. A listed run would make a headless run
        // wait for ever.
        var quarantined = Run("quarantined", 1, new ChildRunLifecycle.Accepted(null));
        var open = Run("open", 2, new ChildRunLifecycle.Accepted(null));
        var state = StateOf(quarantined, open).RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
        {
            Manifest = "test",
            Payload = ImmutableArray<byte>.Empty,
            Disposition = LegacyCoordinationDisposition.UnresolvedLegacyChild,
            ChildRunId = quarantined.RunId
        });

        Assert.Equal(["open"], Ids(state));
    }

    [Fact]
    public void Evidence_that_is_not_an_unresolved_child_does_not_quarantine_a_run()
    {
        var run = Run("kept", 1, new ChildRunLifecycle.Accepted(null));
        var state = StateOf(run).RetainLegacyCoordinationEvidence(new LegacyCoordinationEvidence
        {
            Manifest = "test",
            Payload = ImmutableArray<byte>.Empty,
            Disposition = LegacyCoordinationDisposition.PreservedLegacyFacts,
            ChildRunId = run.RunId
        });

        Assert.Equal(["kept"], Ids(state));
    }

    [Fact]
    public void Runs_are_ordered_by_acceptance_time_then_by_run_id()
    {
        // The first key outranks the second: "z" was accepted first, so it comes first. Two runs with the
        // same time order by id, ordinal and ascending.
        var state = StateOf(
            Run("b", 20, new ChildRunLifecycle.Accepted(null)),
            Run("a", 20, new ChildRunLifecycle.Accepted(null)),
            Run("z", 10, new ChildRunLifecycle.Accepted(null)),
            Run("B", 20, new ChildRunLifecycle.Accepted(null)));

        Assert.Equal(["z", "B", "a", "b"], Ids(state));
    }

    private static string[] Ids(SessionState state) => state.GetOpenChildRunIds().Select(id => id.Value).ToArray();

    private static SessionState StateOf(params BackgroundChildRun[] runs)
        => SessionState.Empty with { ChildRuns = runs.ToImmutableDictionary(run => run.RunId) };

    private static BackgroundChildRun Run(string id, long acceptedAtMs, ChildRunLifecycle lifecycle)
        => new()
        {
            RunId = new SubAgentRunId(id),
            ScopeId = new SubAgentScopeId($"scope-{id}"),
            StartKey = new ChildRunStartKey.Slash(Owner, "turn-1", new InputId($"input-{id}")),
            AgentName = new AgentName("worker"),
            SourceOperation = "slash",
            ArgumentsDigest = $"digest-{id}",
            OriginalContext = new TurnContextRecord
            {
                SessionId = Owner,
                TurnId = "turn-1",
                Boundary = TrustBoundary.Public,
                RequesterPrincipal = PrincipalClassification.UntrustedExternal
            },
            InitialWorkingSnapshot = null,
            AcceptedAtMs = acceptedAtMs,
            Lifecycle = lifecycle,
            SourceReminderId = null
        };

    private static ChildRunLifecycle.Closed Closed()
        => new(new ToolCallId("child-result-call"), SubAgentRunOutcome.Completed, null);

    private static ChildRunTerminal Terminal()
        => new(
            new SubAgentProtocol.SubAgentResult
            {
                AgentName = new AgentName("worker"),
                Output = "Done.",
                Completion = new ChildRunCompletion.Completed(new WorkingContextDelta())
            },
            Lost: false,
            EvidenceWarning: null);

    private static ChildPreparedResult Delivery(ChildRunTerminal terminal)
        => new(
            terminal,
            new SerializableChatMessage { Role = ChatRole.Assistant, Content = "call" },
            new SerializableChatMessage { Role = ChatRole.Tool, Content = "result" },
            null);
}
