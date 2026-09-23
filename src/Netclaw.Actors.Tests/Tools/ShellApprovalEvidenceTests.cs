// -----------------------------------------------------------------------
// <copyright file="ShellApprovalEvidenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ShellApprovalEvidenceTests
{
    [Theory]
    [InlineData(ApprovalShell.Bash, "/")]
    [InlineData(ApprovalShell.Bash, "/work/repo")]
    [InlineData(ApprovalShell.PowerShell, "C:\\")]
    [InlineData(ApprovalShell.PowerShell, "C:\\work\\repo")]
    [InlineData(ApprovalShell.PowerShell, "\\\\server\\share")]
    [InlineData(ApprovalShell.PowerShell, "\\\\server\\share\\repo")]
    [InlineData(ApprovalShell.PowerShell, "\\\\?\\C:\\repo")]
    public void Canonical_persistent_scope_remains_valid(
        ApprovalShell shell,
        string directory)
    {
        var verb = shell == ApprovalShell.Bash ? "git status" : "Get-Location";
        var candidate = CreateCandidate(0, shell, verb, directory);
        var grantCandidate = CreateGrantCandidate(candidate, directory);
        var entry = ApprovalEntry.CreateTokenPrefix(
            shell,
            Assert.IsAssignableFrom<IReadOnlyList<string>>(candidate.Candidate.VerbTokens),
            directory);
        var result = ShellApprovalMatchResult.Create(
            [grantCandidate],
            persistentStoreFailure: null,
            [ShellGrantCandidateResult.Persistent(grantCandidate, entry)]);

        var match = Assert.Single(result.Candidates);
        Assert.Equal(ShellCoverageKind.PersistentFolder, match.Coverage);
        Assert.Equal(entry.FormatScope(), match.FormatMatch(candidate.Candidate).Scope);
    }

    [Theory]
    [InlineData(
        ApprovalShell.Bash,
        "git status",
        "/danger",
        "Bash token-prefix \"git status\" in /safe/../danger")]
    [InlineData(
        ApprovalShell.PowerShell,
        "Get-Location",
        "C:\\danger",
        "PowerShell token-prefix \"Get-Location\" in C:\\safe\\..\\danger")]
    [InlineData(
        ApprovalShell.Bash,
        "git status",
        null,
        "Bash token-prefix \"git  status\" anywhere")]
    public async Task Persistent_scope_must_be_canonical(
        ApprovalShell shell,
        string verb,
        string? directory,
        string scope)
    {
        var candidate = CreateCandidate(0, shell, verb, directory);
        var grantCandidate = CreateGrantCandidate(candidate, directory);
        var publicMatch = new ToolApprovalMatch(verb, "persistent", scope);
        var publicResult = new ToolApprovalCheckResult([], [publicMatch])
        {
            CandidateChecks =
            [
                new ToolApprovalCandidateCheck(candidate.Candidate, publicMatch)
            ]
        };
        var adapter = new ShellApprovalEvidenceAdapter(new FixedApprovalService(publicResult));
        var request = new ShellApprovalMatchRequest(
            SessionId: null,
            TrustAudience.Personal,
            new ToolName("shell_execute"),
            TestShellEnvironment.Current,
            [grantCandidate]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.MatchAsync(request, directory, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(MalformedNearMissGrantCase.InvalidShell)]
    [InlineData(MalformedNearMissGrantCase.InvalidMatch)]
    [InlineData(MalformedNearMissGrantCase.NonShell)]
    public void Malformed_near_miss_grant_invalidates_the_whole_batch(
        MalformedNearMissGrantCase malformedCase)
    {
        var covered = CreateCandidate(0, ApprovalShell.Bash, "git status", null);
        var uncovered = CreateCandidate(1, ApprovalShell.Bash, "git push", null);
        var coveredGrantCandidate = CreateGrantCandidate(covered, directory: null);
        var uncoveredGrantCandidate = CreateGrantCandidate(uncovered, directory: null);
        var typedGrant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["git", "status"]);
        var malformedGrant = malformedCase switch
        {
            MalformedNearMissGrantCase.InvalidShell => typedGrant with
            {
                Shell = (ApprovalShell)999
            },
            MalformedNearMissGrantCase.InvalidMatch => typedGrant with
            {
                Match = (ApprovalMatchKind)999
            },
            MalformedNearMissGrantCase.NonShell => ApprovalEntry.CreateNonShell(
                "git status",
                "/outside"),
            _ => throw new ArgumentOutOfRangeException(nameof(malformedCase), malformedCase, null)
        };

        var exception = Record.Exception(() => ShellApprovalMatchResult.Create(
            [coveredGrantCandidate, uncoveredGrantCandidate],
            persistentStoreFailure: null,
            [
                ShellGrantCandidateResult.Session(coveredGrantCandidate),
                ShellGrantCandidateResult.Uncovered(
                    uncoveredGrantCandidate,
                    new ShellApprovalNearMiss(
                        malformedGrant,
                        ShellApprovalNearMissReason.ShellMismatch))
            ]));

        if (malformedCase == MalformedNearMissGrantCase.NonShell)
            Assert.IsType<ArgumentException>(exception);
        else
            Assert.IsType<System.Text.Json.JsonException>(exception);
    }

    private static ShellPolicyCandidate CreateCandidate(
        int id,
        ApprovalShell shell,
        string verb,
        string? directory)
        => new(
            new ShellPolicyCandidateId(id),
            new ApprovalCandidate(verb, directory)
            {
                Shell = shell,
                VerbTokens = Array.AsReadOnly(
                    verb.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            },
            SourceOccurrence: null);

    private static ShellGrantCandidate CreateGrantCandidate(
        ShellPolicyCandidate candidate,
        string? directory)
        => new(candidate.Id, candidate.Candidate, directory);

    private sealed class FixedApprovalService(ToolApprovalCheckResult result) : IToolApprovalService
    {
        public Task<ToolApprovalCheckResult> CheckApprovalAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<ApprovalCandidate> candidates,
            string? cwd,
            CancellationToken ct = default)
            => Task.FromResult(result);

        public Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("This test uses the candidate check API.");

        public Task RecordApprovalAsync(
            ToolApprovalSessionId sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            bool persistent,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("This test does not record approvals.");
    }

    public enum MalformedNearMissGrantCase
    {
        InvalidShell,
        InvalidMatch,
        NonShell,
    }
}
