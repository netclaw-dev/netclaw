// -----------------------------------------------------------------------
// <copyright file="BackgroundJobSessionStateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class BackgroundJobSessionStateTests
{
    [Fact]
    public void ActiveBackgroundJobs_RoundTrips_ThroughSnapshot()
    {
        var jobKey = "bg-job:abc123";
        var info = new ActiveJobInfo
        {
            JobId = new Netclaw.Actors.Jobs.BackgroundJobId("abc123"),
            Command = "make build",
            Rationale = "building project",
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal
        };

        var state = SessionState.Empty.TrackBackgroundJob(jobKey, info);
        Assert.Single(state.ActiveBackgroundJobs);

        var snapshot = state.ToSnapshot();
        Assert.Single(snapshot.ActiveBackgroundJobs);

        var recovered = SessionState.FromSnapshot(snapshot);
        Assert.Single(recovered.ActiveBackgroundJobs);
        Assert.True(recovered.ActiveBackgroundJobs.ContainsKey(jobKey));
        Assert.Equal("abc123", recovered.ActiveBackgroundJobs[jobKey].JobId.Value);
        Assert.Equal("make build", recovered.ActiveBackgroundJobs[jobKey].Command);
    }

    [Fact]
    public void TurnRecorded_WithSourceBackgroundJobId_DedupAndRemovesActive()
    {
        var jobKey = "bg-job:abc123";
        var info = new ActiveJobInfo
        {
            JobId = new Netclaw.Actors.Jobs.BackgroundJobId("abc123"),
            Command = "make build",
            Rationale = "building project",
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public
        };

        var state = SessionState.Empty.TrackBackgroundJob(jobKey, info);
        Assert.Single(state.ActiveBackgroundJobs);
        Assert.Empty(state.ProcessedBackgroundJobIds);

        var evt = new TurnRecorded
        {
            SessionId = new SessionId("test/thread"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "result" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "ok" },
            RecordedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SourceBackgroundJobId = new BackgroundJobId(jobKey)
        };

        state = state.Apply(evt);

        Assert.Empty(state.ActiveBackgroundJobs);
        Assert.Contains(new BackgroundJobId(jobKey), state.ProcessedBackgroundJobIds);
    }

    [Fact]
    public void ReapedMarks_RoundTrip_ThroughSnapshot()
    {
        var jobKey = "bg-job:reap01";
        var info = new ActiveJobInfo
        {
            JobId = new Netclaw.Actors.Jobs.BackgroundJobId("reap01"),
            Command = "jekyll serve",
            Rationale = "dev server",
            StartedAtMs = 1000,
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            OutputLogPath = "/tmp/jobs/reap01/output.log"
        };

        var state = SessionState.Empty
            .TrackBackgroundJob(jobKey, info)
            .MarkAllBackgroundJobsReaped(2000);

        var recovered = SessionState.FromSnapshot(state.ToSnapshot());

        var job = recovered.ActiveBackgroundJobs[jobKey];
        Assert.Equal(2000, job.ReapedAtMs);
        Assert.Equal("/tmp/jobs/reap01/output.log", job.OutputLogPath);
    }

    [Fact]
    public void TurnRecorded_PrunesReapedEntries_ButKeepsLiveOnes()
    {
        // A reaped entry was surfaced in the turn's context block; pruning on
        // TurnRecorded means the agent hears about the reap exactly once.
        var live = new ActiveJobInfo
        {
            JobId = new Netclaw.Actors.Jobs.BackgroundJobId("live01"),
            Command = "make build",
            Rationale = "building",
            StartedAtMs = 1000,
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal
        };
        var reaped = live with
        {
            JobId = new Netclaw.Actors.Jobs.BackgroundJobId("dead01"),
            ReapedAtMs = 2000
        };

        var state = SessionState.Empty
            .TrackBackgroundJob("bg-job:live01", live)
            .TrackBackgroundJob("bg-job:dead01", reaped);

        var evt = new TurnRecorded
        {
            SessionId = new SessionId("test/thread"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "hi" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "ok" },
            RecordedAtMs = 3000
        };

        state = state.Apply(evt);

        Assert.True(state.ActiveBackgroundJobs.ContainsKey("bg-job:live01"));
        Assert.False(state.ActiveBackgroundJobs.ContainsKey("bg-job:dead01"));
    }

    [Fact]
    public void SessionBackgroundJobsReaped_event_marks_all_active_jobs_reaped()
    {
        // The journaled reap event (replayed on recovery when the passivation
        // snapshot was skipped) must mark every tracked job reaped so the
        // context block does not rehydrate killed jobs as "running".
        var a = new ActiveJobInfo
        {
            JobId = new Netclaw.Actors.Jobs.BackgroundJobId("a"),
            Command = "jekyll serve",
            Rationale = "dev server",
            StartedAtMs = 1000,
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal
        };
        var b = a with { JobId = new Netclaw.Actors.Jobs.BackgroundJobId("b") };

        var state = SessionState.Empty
            .TrackBackgroundJob("bg-job:a", a)
            .TrackBackgroundJob("bg-job:b", b)
            .Apply(new SessionBackgroundJobsReaped
            {
                SessionId = new SessionId("test/thread"),
                ReapedAtMs = 5000
            });

        Assert.Equal(5000, state.ActiveBackgroundJobs["bg-job:a"].ReapedAtMs);
        Assert.Equal(5000, state.ActiveBackgroundJobs["bg-job:b"].ReapedAtMs);
    }

    [Fact]
    public void MarkAllBackgroundJobsReaped_PreservesEarlierReapTimestamp()
    {
        var info = new ActiveJobInfo
        {
            JobId = new Netclaw.Actors.Jobs.BackgroundJobId("reap02"),
            Command = "npm run dev",
            Rationale = "dev server",
            StartedAtMs = 1000,
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            ReapedAtMs = 1500
        };

        var state = SessionState.Empty
            .TrackBackgroundJob("bg-job:reap02", info)
            .MarkAllBackgroundJobsReaped(9999);

        Assert.Equal(1500, state.ActiveBackgroundJobs["bg-job:reap02"].ReapedAtMs);
    }

    [Fact]
    public void Compaction_Preserves_ActiveJobsAndDedupSet()
    {
        var jobKey = "bg-job:def456";
        var info = new ActiveJobInfo
        {
            JobId = new Netclaw.Actors.Jobs.BackgroundJobId("def456"),
            Command = "make test",
            Rationale = "running tests",
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public
        };

        var state = SessionState.Empty.TrackBackgroundJob(jobKey, info);

        var processedKey = "bg-job:already-done";
        state = state with
        {
            ProcessedBackgroundJobIds = state.ProcessedBackgroundJobIds.Add(new BackgroundJobId(processedKey))
        };

        var compactedEvt = new SessionCompacted
        {
            SessionId = new SessionId("test/thread"),
            Summary = "compacted",
            CompactedMessages = [],
            TurnCountBefore = 10,
            CompactedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        state = state.Apply(compactedEvt);

        Assert.Single(state.ActiveBackgroundJobs);
        Assert.True(state.ActiveBackgroundJobs.ContainsKey(jobKey));
        Assert.Contains(new BackgroundJobId(processedKey), state.ProcessedBackgroundJobIds);
    }
}
