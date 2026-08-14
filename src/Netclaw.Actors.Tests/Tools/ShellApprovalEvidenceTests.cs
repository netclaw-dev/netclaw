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
        var entry = ApprovalEntry.CreateTokenPrefix(
            shell,
            Assert.IsAssignableFrom<IReadOnlyList<string>>(candidate.Candidate.VerbTokens),
            directory);
        var result = new ShellApprovalMatchResult(
            new PersistentGrantStoreStatus.Ready(),
            [
                new ShellGrantCandidateMatch(
                    candidate.Id,
                    new ToolApprovalMatch(verb, "persistent", entry.FormatScope()),
                    ShellCoverageKind.PersistentFolder,
                    NearMisses: [])
            ]);

        var valid = ValidatedShellGrantEvidence.TryCreate(
            result,
            [candidate],
            directory,
            out var evidence);

        Assert.True(valid);
        Assert.NotNull(evidence);
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
    public void Persistent_scope_must_be_canonical(
        ApprovalShell shell,
        string verb,
        string? directory,
        string scope)
    {
        var candidate = CreateCandidate(0, shell, verb, directory);
        var result = new ShellApprovalMatchResult(
            new PersistentGrantStoreStatus.Ready(),
            [
                new ShellGrantCandidateMatch(
                    candidate.Id,
                    new ToolApprovalMatch(verb, "persistent", scope),
                    directory is null
                        ? ShellCoverageKind.PersistentGlobal
                        : ShellCoverageKind.PersistentFolder,
                    NearMisses: [])
            ]);

        var valid = ValidatedShellGrantEvidence.TryCreate(
            result,
            [candidate],
            directory,
            out var evidence);

        Assert.False(valid);
        Assert.Null(evidence);
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
        var result = new ShellApprovalMatchResult(
            new PersistentGrantStoreStatus.Ready(),
            [
                new ShellGrantCandidateMatch(
                    covered.Id,
                    new ToolApprovalMatch(covered.Candidate.Verb, "session", "this chat"),
                    ShellCoverageKind.Session,
                    NearMisses: []),
                new ShellGrantCandidateMatch(
                    uncovered.Id,
                    Match: null,
                    GrantCoverage: null,
                    NearMisses:
                    [
                        new ShellApprovalNearMiss(
                            malformedGrant,
                            ShellApprovalNearMissReason.ShellMismatch)
                    ])
            ]);

        var valid = ValidatedShellGrantEvidence.TryCreate(
            result,
            [covered, uncovered],
            cwd: null,
            out var evidence);

        Assert.False(valid);
        Assert.Null(evidence);
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

    public enum MalformedNearMissGrantCase
    {
        InvalidShell,
        InvalidMatch,
        NonShell,
    }
}
