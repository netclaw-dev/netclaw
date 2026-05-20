// -----------------------------------------------------------------------
// <copyright file="BuildApprovalBucketsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Pinning the bucketing branch of <c>LlmSessionActor.PersistApprovalCandidatesAsync</c>
/// that decides which approval candidates make it into the persistence call.
/// The session-scope vs persistent-scope branching is where a bug class lived:
/// standalone verbs with no anchored path argument (curl https://..., gh pr
/// list, git status) used to inherit the session_dir as their effective
/// directory and then get silently dropped by the session-scratch
/// dead-on-arrival guard. The retry would then fail to find the verb in
/// ToolApprovalActor._sessionApprovals and throw
/// ToolApprovalRequiredException, surfacing as
/// "I encountered an error executing a tool."
/// </summary>
public sealed class BuildApprovalBucketsTests
{
    private const string SessionDir = "/home/user/.netclaw/sessions/abc";
    private const string ProjectDir = "/home/user/repos/example";

    [Fact]
    public void Session_scope_drops_cwd_fallback_so_no_path_arg_verbs_persist()
    {
        // Production repro: 4 parallel curl tool calls in one batch, all
        // ApprovedSession. Each has candidate.Directory == null (curl URL
        // is not an anchored path). Before the fix, persistence resolved
        // effectiveDirectory = null ?? pending.Cwd = session_dir, the
        // session-scratch guard fired, the verb never landed in the
        // session approval dict, and the retry threw.
        var candidates = new[]
        {
            new ApprovalCandidate("curl", null)
        };

        var buckets = ApprovalBucketBuilder.Build(
            candidates,
            persistent: false,
            globalWildcard: false,
            cwd: SessionDir,
            sessionDirectory: SessionDir);

        var bucket = Assert.Single(buckets);
        Assert.Equal(string.Empty, bucket.Key);  // null-directory bucket
        Assert.Contains("curl", bucket.Value);
    }

    [Fact]
    public void Session_scope_groups_verbs_with_concrete_directory_into_that_directory_bucket()
    {
        // Mixed compound where both candidates name a real directory:
        // session-scope preserves each candidate's Directory verbatim
        // (no cwd fallback) so the bucket key reflects the actual
        // operand path rather than session_dir.
        var candidates = new[]
        {
            new ApprovalCandidate("cd", ProjectDir),
            new ApprovalCandidate("git checkout", ProjectDir)
        };

        var buckets = ApprovalBucketBuilder.Build(
            candidates,
            persistent: false,
            globalWildcard: false,
            cwd: SessionDir,
            sessionDirectory: SessionDir);

        var bucket = Assert.Single(buckets);
        Assert.Equal(ProjectDir, bucket.Key);
        Assert.Contains("cd", bucket.Value);
        Assert.Contains("git checkout", bucket.Value);
    }

    [Fact]
    public void Persistent_scope_drops_candidates_resolving_to_session_dir()
    {
        // The original design intent: a folder-scoped persistent entry
        // whose effective directory IS the session_dir is dead on
        // arrival, because the next session has a fresh session_dir.
        // The guard remains in place for persistent scope.
        var candidates = new[]
        {
            new ApprovalCandidate("curl", null)  // no path arg → falls back to cwd
        };

        var buckets = ApprovalBucketBuilder.Build(
            candidates,
            persistent: true,
            globalWildcard: false,
            cwd: SessionDir,
            sessionDirectory: SessionDir);

        Assert.Empty(buckets);
    }

    [Fact]
    public void Persistent_scope_keeps_candidates_with_concrete_directory()
    {
        var candidates = new[]
        {
            new ApprovalCandidate("git checkout", ProjectDir)
        };

        var buckets = ApprovalBucketBuilder.Build(
            candidates,
            persistent: true,
            globalWildcard: false,
            cwd: SessionDir,
            sessionDirectory: SessionDir);

        var bucket = Assert.Single(buckets);
        Assert.Equal(ProjectDir, bucket.Key);
        Assert.Contains("git checkout", bucket.Value);
    }

    [Fact]
    public void Global_wildcard_writes_null_directory_regardless_of_scope()
    {
        // ApprovedEverywhere → directory is null on disk so the entry
        // matches any cwd at future evaluation. cwd is irrelevant here.
        var candidates = new[]
        {
            new ApprovalCandidate("git push origin main", ProjectDir),
            new ApprovalCandidate("curl", null)
        };

        var buckets = ApprovalBucketBuilder.Build(
            candidates,
            persistent: true,
            globalWildcard: true,
            cwd: SessionDir,
            sessionDirectory: SessionDir);

        var bucket = Assert.Single(buckets);
        Assert.Equal(string.Empty, bucket.Key);
        Assert.Contains("git push origin main", bucket.Value);
        Assert.Contains("curl", bucket.Value);
    }

    [Fact]
    public void Pure_side_effect_verbs_are_dropped_from_persistence_at_either_scope()
    {
        // echo / printf / true / false are authorized for the current
        // call but never persisted. Mirrors the IsPureSideEffect skip
        // in ApprovalPatternMatching.MatchesShellApproval at lookup time.
        var candidates = new[]
        {
            new ApprovalCandidate("echo", null),
            new ApprovalCandidate("git status", null)
        };

        var sessionBuckets = ApprovalBucketBuilder.Build(
            candidates,
            persistent: false,
            globalWildcard: false,
            cwd: SessionDir,
            sessionDirectory: SessionDir);

        var bucket = Assert.Single(sessionBuckets);
        Assert.DoesNotContain("echo", bucket.Value);
        Assert.Contains("git status", bucket.Value);
    }
}
