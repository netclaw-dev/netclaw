// -----------------------------------------------------------------------
// <copyright file="ChildRunReducerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Tests.Protocol.ChildRunParkSerializationTests;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// running-child-drain-park design D1 (R1 and R4). The child-run reducer takes a park with zero requests (a drain park)
/// and a park with consent requests through one rule. The resume text follows the request count of the park. A replay
/// that throws stops the session, so each refused row is a loud refusal and not a silent skip.
/// </summary>
public sealed class ChildRunReducerTests
{
    private const int PromptsIssued = 2;

    /// <summary>
    /// Rows:
    /// <list type="bullet">
    /// <item>drain-park: Accepted + Parked with 0 requests gives Parked with no request. The prompt counter stays.</item>
    /// <item>drain-resume: Parked with 0 requests + Resumed gives Accepted at the round of the park, with the restart
    /// text.</item>
    /// <item>consent-resume: Parked with 1 answered request + Resumed gives Accepted with the consent text.</item>
    /// <item>consent-open: Parked with 1 open request + Resumed throws.</item>
    /// <item>foreign-request: a park whose request names another run throws.</item>
    /// </list>
    /// Planted bug: a replay of a drain park stops the session, a drain park moves the prompt counter, a drain resume
    /// says that consent resumed it, or a resume skips an open request.
    /// </summary>
    [Theory]
    [InlineData("drain-park")]
    [InlineData("drain-resume")]
    [InlineData("consent-resume")]
    [InlineData("consent-open")]
    [InlineData("foreign-request")]
    public void A_park_and_its_resume_follow_the_request_count(string row)
    {
        // Arrange
        var park = FullPark();
        var resumed = new ChildRunEvent.Resumed { SessionId = Owner, RunId = RunId, RecordedAtMs = 20 };
        var (lifecycle, evt) = row switch
        {
            "drain-park" => ((ChildRunLifecycle)new ChildRunLifecycle.Accepted(null), (ChildRunEvent)Parked(park, [])),
            "drain-resume" => (new ChildRunLifecycle.Parked(park, []), resumed),
            "consent-resume" => (new ChildRunLifecycle.Parked(park, [new ChildParkRequest(Request(1), Resolution(1))]), resumed),
            "consent-open" => (new ChildRunLifecycle.Parked(park, [new ChildParkRequest(Request(1), null)]), resumed),
            "foreign-request" => (new ChildRunLifecycle.Accepted(null),
                Parked(park, [Request(1) with { SourceChildRunId = new SubAgentRunId("other-run") }])),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, null)
        };
        var state = SessionState.Empty with
        {
            ChildRuns = ImmutableDictionary<SubAgentRunId, BackgroundChildRun>.Empty
                .Add(RunId, Run(lifecycle) with { PromptsIssued = PromptsIssued })
        };

        // Act + Assert
        if (row is "consent-open" or "foreign-request")
        {
            Assert.Throws<InvalidOperationException>(() => state.Apply(evt));
            return;
        }

        var run = state.Apply(evt).ChildRuns[RunId];
        var round = park.LoopGuard.ToolIterationCount;
        ChildRunLifecycle expected = row switch
        {
            "drain-park" => new ChildRunLifecycle.Parked(park, ImmutableArray<ChildParkRequest>.Empty),
            "drain-resume" => new ChildRunLifecycle.Accepted(
                new ChildRunCheckpoint(round, "The child resumed after the restart.", park.FileActivity)),
            _ => new ChildRunLifecycle.Accepted(new ChildRunCheckpoint(round, "The child resumed after consent.", park.FileActivity))
        };
        Assert.IsType(expected.GetType(), run.Lifecycle);
        Assert.Equivalent(expected, run.Lifecycle, strict: true);
        Assert.Equal(PromptsIssued, run.PromptsIssued);
    }

    private static ChildRunEvent.Parked Parked(ChildRunPark park, IReadOnlyList<ToolApprovalRequested> requests)
        => new(park, requests) { SessionId = Owner, RunId = RunId, RecordedAtMs = 10 };
}
