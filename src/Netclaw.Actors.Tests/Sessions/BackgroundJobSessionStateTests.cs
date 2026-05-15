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

namespace Netclaw.Actors.Tests.Sessions;

public sealed class BackgroundJobSessionStateTests
{
    [Fact]
    public void ActiveBackgroundJobs_RoundTrips_ThroughSnapshot()
    {
        var jobKey = "bg-job:abc123";
        var info = new ActiveJobInfo
        {
            JobId = "abc123",
            Command = "make build",
            Rationale = "building project",
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.PersonalBoundary
        };

        var state = SessionState.Empty.TrackBackgroundJob(jobKey, info);
        Assert.Single(state.ActiveBackgroundJobs);

        var snapshot = state.ToSnapshot();
        Assert.Single(snapshot.ActiveBackgroundJobs);

        var recovered = SessionState.FromSnapshot(snapshot);
        Assert.Single(recovered.ActiveBackgroundJobs);
        Assert.True(recovered.ActiveBackgroundJobs.ContainsKey(jobKey));
        Assert.Equal("abc123", recovered.ActiveBackgroundJobs[jobKey].JobId);
        Assert.Equal("make build", recovered.ActiveBackgroundJobs[jobKey].Command);
    }

    [Fact]
    public void TurnRecorded_WithSourceBackgroundJobId_DedupAndRemovesActive()
    {
        var jobKey = "bg-job:abc123";
        var info = new ActiveJobInfo
        {
            JobId = "abc123",
            Command = "make build",
            Rationale = "building project",
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Audience = TrustAudience.Public,
            Boundary = SecurityPolicyDefaults.PublicBoundary
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
            SourceBackgroundJobId = jobKey
        };

        state = state.Apply(evt);

        Assert.Empty(state.ActiveBackgroundJobs);
        Assert.Contains(jobKey, state.ProcessedBackgroundJobIds);
    }

    [Fact]
    public void Compaction_Preserves_ActiveJobsAndDedupSet()
    {
        var jobKey = "bg-job:def456";
        var info = new ActiveJobInfo
        {
            JobId = "def456",
            Command = "make test",
            Rationale = "running tests",
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Audience = TrustAudience.Public,
            Boundary = SecurityPolicyDefaults.PublicBoundary
        };

        var state = SessionState.Empty.TrackBackgroundJob(jobKey, info);

        var processedKey = "bg-job:already-done";
        state = state with
        {
            ProcessedBackgroundJobIds = state.ProcessedBackgroundJobIds.Add(processedKey)
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
        Assert.Contains(processedKey, state.ProcessedBackgroundJobIds);
    }
}
