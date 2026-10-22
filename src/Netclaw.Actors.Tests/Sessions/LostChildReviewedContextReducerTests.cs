// -----------------------------------------------------------------------
// <copyright file="LostChildReviewedContextReducerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Reducer specs for issue 2602, option B of the fix review. A lost run with a checkpoint is a partial run with the
/// reason <c>owner_restarted</c>. It is not a success, but it has a delta. The reducer accepts a reviewed context on its
/// notice and merges the confirmed changes into the parent working context. The reducer refuses a reviewed context only
/// when the completion of the run has no delta.
/// </summary>
public sealed class LostChildReviewedContextReducerTests
{
    private static readonly SessionId Owner = new("test/lost-child-reducer");
    private static readonly SubAgentOutcomeReason OwnerRestarted = new("owner_restarted");
    private const string ChangedFile = "/repo/changed-before-restart.cs";

    /// <summary>
    /// A notice of a lost partial run carries a reviewed context. The reducer accepts it, reports the run with its
    /// outcome and reason, and puts the confirmed changed file into the recent files of the parent.
    /// Planted bug: the reducer refuses a reviewed context on each run that is not a success.
    /// </summary>
    [Fact]
    public void A_notice_of_a_lost_partial_run_applies_its_reviewed_context()
    {
        // Arrange
        var (state, run) = LostPartialRun();
        var reviewed = new WorkingContextDelta { ConfirmedChangedFiles = [ChangedFile] };

        // Act
        var admitted = state.Apply(Notice(run, reviewed, "notice-1"));

        // Assert
        var reported = Assert.IsType<ChildRunLifecycle.Reported>(admitted.ChildRuns[run.RunId].Lifecycle);
        Assert.Equal(SubAgentRunOutcome.Partial, reported.Outcome);
        Assert.Equal(OwnerRestarted, reported.Reason);
        Assert.Contains(ChangedFile, admitted.WorkingContext.RecentFiles);
    }

    /// <summary>
    /// Guard. A failed run has no delta, so a notice with a reviewed context stays refused.
    /// </summary>
    [Fact]
    public void A_notice_of_a_lost_run_with_no_delta_cannot_carry_a_reviewed_context()
    {
        // Arrange
        var run = Run();
        var state = SessionState.Empty.Apply(new ChildRunAccepted { SessionId = Owner, Run = run, RecordedAtMs = 1 })
            .Apply(Terminal(run, new ChildRunCompletion.Failed(OwnerRestarted)));

        // Act and Assert
        var error = Assert.Throws<InvalidOperationException>(() =>
            state.Apply(Notice(run, new WorkingContextDelta { ConfirmedChangedFiles = [ChangedFile] }, "notice-1")));
        Assert.Contains("reviewed working context", error.Message, StringComparison.Ordinal);
    }

    private static (SessionState State, BackgroundChildRun Run) LostPartialRun()
    {
        var run = Run();
        var delta = new WorkingContextDelta { ConfirmedChangedFiles = [ChangedFile] };
        var state = SessionState.Empty
            .Apply(new ChildRunAccepted { SessionId = Owner, Run = run, RecordedAtMs = 1 })
            .Apply(Terminal(run, new ChildRunCompletion.Partial(OwnerRestarted, delta)));
        return (state, run);
    }

    private static ChildRunEvent.TerminalRecorded Terminal(BackgroundChildRun run, ChildRunCompletion completion)
    {
        var result = new SubAgentProtocol.SubAgentResult
        {
            AgentName = run.AgentName,
            RunId = run.RunId,
            ScopeId = run.ScopeId,
            Output = "The child did not survive a restart.",
            Completion = completion
        };
        return new ChildRunEvent.TerminalRecorded(new ChildRunTerminal(result, Lost: true, EvidenceWarning: null))
        {
            SessionId = Owner,
            RunId = run.RunId,
            RecordedAtMs = 2
        };
    }

    private static InputAdmitted Notice(BackgroundChildRun run, WorkingContextDelta? delta, string inputId) => new()
    {
        SessionId = Owner,
        InputId = new InputId(inputId),
        UserMessage = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = $"Child run ended. run={run.RunId.Value} outcome=partial reason=owner_restarted result_file=/x/result.md"
        },
        TurnContext = run.OriginalContext with { TurnId = $"turn-{inputId}" },
        ChildNotice = new ChildNotice(run.RunId, delta),
        AdmittedAtMs = 3
    };

    private static BackgroundChildRun Run() => new()
    {
        RunId = new SubAgentRunId("lost-run"),
        ScopeId = new SubAgentScopeId("lost-scope"),
        StartKey = new ChildRunStartKey.Slash(Owner, "turn-1", new InputId("slash-input")),
        AgentName = new AgentName("worker"),
        SourceOperation = "slash",
        ArgumentsDigest = "digest-1",
        OriginalContext = new TurnContextRecord
        {
            SessionId = Owner,
            TurnId = "turn-1",
            Boundary = TrustBoundary.Personal,
            RequesterSenderId = new SenderId("alice"),
            RequesterPrincipal = PrincipalClassification.Operator
        },
        InitialWorkingSnapshot = new WorkingContextSnapshot
        {
            WorkingContext = new WorkingContext { ProjectDirectory = "/repo/before" },
            Git = new GitWorkingContextInspection.Skipped()
        },
        AcceptedAtMs = 1,
        Lifecycle = new ChildRunLifecycle.Accepted(null),
        SourceReminderId = null
    };
}
