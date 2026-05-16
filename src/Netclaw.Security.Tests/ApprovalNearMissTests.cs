// -----------------------------------------------------------------------
// <copyright file="ApprovalNearMissTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Tests for <see cref="ApprovalPatternMatching.ExplainShellNearMisses"/>,
/// the read-only diagnostic that explains why a persisted grant for the same
/// verb failed to auto-approve a candidate.
/// </summary>
public sealed class ApprovalNearMissTests
{
    private static ApprovalEntry InDir(string verb, string dir) => new() { Verb = verb, Directory = dir };

    private static ApprovalEntry Verb(string verb) => new() { Verb = verb, Directory = null };

    [Fact]
    public void DirectoryNotUnderGrant_is_reported_when_cwd_is_outside_the_grant()
    {
        var grant = InDir("git push", "/home/user/repos/foo");

        var misses = ApprovalPatternMatching.ExplainShellNearMisses(
            "git push", candidateDirectory: null, cwd: "/home/user/repos/bar", [grant]);

        var miss = Assert.Single(misses);
        Assert.Equal(ApprovalNearMissReason.DirectoryNotUnderGrant, miss.Reason);
        Assert.Same(grant, miss.Grant);
        Assert.Contains("/home/user/repos/foo", miss.Describe());
    }

    [Fact]
    public void No_near_miss_when_no_persisted_entry_shares_the_verb()
    {
        var unrelated = InDir("npm install", "/home/user/repos/foo");

        var misses = ApprovalPatternMatching.ExplainShellNearMisses(
            "git push", candidateDirectory: null, cwd: "/home/user/repos/bar", [unrelated]);

        Assert.Empty(misses);
    }

    [Fact]
    public void NoCandidateDirectory_is_reported_for_a_folder_scoped_grant()
    {
        var grant = InDir("git push", "/home/user/repos/foo");

        var misses = ApprovalPatternMatching.ExplainShellNearMisses(
            "git push", candidateDirectory: null, cwd: null, [grant]);

        var miss = Assert.Single(misses);
        Assert.Equal(ApprovalNearMissReason.NoCandidateDirectory, miss.Reason);
    }

    [Fact]
    public void VerbCaseMismatch_is_reported_on_case_sensitive_filesystems()
    {
        // A global-wildcard grant for "Git" isolates the verb-case logic from
        // any directory comparison.
        var grant = Verb("Git");

        var misses = ApprovalPatternMatching.ExplainShellNearMisses(
            "git", candidateDirectory: null, cwd: "/home/user/repos/foo", [grant]);

        if (OperatingSystem.IsWindows())
        {
            // Windows folds case in the platform comparer, so the wildcard
            // grant matches outright — there is nothing to explain.
            Assert.Empty(misses);
        }
        else
        {
            var miss = Assert.Single(misses);
            Assert.Equal(ApprovalNearMissReason.VerbCaseMismatch, miss.Reason);
        }
    }

    [Fact]
    public void SymlinkSegment_breaks_the_match_and_is_reported_as_a_near_miss()
    {
        // CreateSymbolicLink without elevation requires Developer Mode on
        // Windows; POSIX is sufficient for regression coverage.
        if (OperatingSystem.IsWindows())
            return;

        var grantRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(grantRoot);
        var leak = Path.Combine(grantRoot, "leak");
        Directory.CreateSymbolicLink(leak, "/etc");

        try
        {
            var grant = InDir("cat", grantRoot);

            var misses = ApprovalPatternMatching.ExplainShellNearMisses(
                "cat", candidateDirectory: null, cwd: leak, [grant]);

            var miss = Assert.Single(misses);
            Assert.Equal(ApprovalNearMissReason.SymlinkSegmentOnPath, miss.Reason);
        }
        finally
        {
            File.Delete(leak);
            Directory.Delete(grantRoot);
        }
    }

    [Fact]
    public void A_matching_global_wildcard_grant_is_not_a_near_miss()
    {
        // A (verb, null) grant would have approved the candidate outright, so
        // it must never surface as a near-miss.
        var misses = ApprovalPatternMatching.ExplainShellNearMisses(
            "git push", candidateDirectory: null, cwd: "/anywhere", [Verb("git push")]);

        Assert.Empty(misses);
    }
}
