// -----------------------------------------------------------------------
// <copyright file="GrantBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Pins which grants an operator answer stores. The session-scope versus
/// persistent-scope branch is where a bug class lived: standalone verbs with
/// no anchored path argument (curl https://..., gh pr list, git status) used
/// to inherit the session directory as their effective directory and then get
/// dropped by the session-owned dead-on-arrival guard. The retry then failed to
/// find the verb in the session approvals and surfaced as
/// "I encountered an error executing a tool."
/// </summary>
public sealed class GrantBuilderTests
{
    private const string SessionDir = "/home/user/.netclaw/sessions/abc";
    private const string ProjectDir = "/home/user/repos/example";

    [Fact]
    public void Session_scope_keeps_verbs_without_a_path_operand()
    {
        // Production repro: 4 parallel curl tool calls in one batch, all
        // approved for this chat. Each has candidate.Directory == null. A
        // session grant must not inherit the session directory.
        var grant = Assert.Single(Build(GrantScopeKind.Session, new ApprovalCandidate("curl", null)));

        Assert.Equal("curl", grant.Candidate.Verb);
        Assert.Equal(GrantScope.Session.Instance, grant.Scope);
    }

    [Fact]
    public void Folder_scope_drops_candidates_resolving_to_the_session_directory()
    {
        // A folder grant whose directory is the session directory is dead on
        // arrival, because the next session has a new session directory.
        Assert.Empty(Build(GrantScopeKind.Folder, new ApprovalCandidate("curl", null)));
    }

    [Fact]
    public void Folder_scope_keeps_candidates_with_a_concrete_directory()
    {
        var grant = Assert.Single(Build(
            GrantScopeKind.Folder,
            new ApprovalCandidate("git checkout", ProjectDir)));

        Assert.Equal(new GrantScope.Folder(ProjectDir), grant.Scope);
    }

    [Fact]
    public void Everywhere_scope_ignores_every_directory()
    {
        var grants = Build(
            GrantScopeKind.Everywhere,
            new ApprovalCandidate("git push origin main", ProjectDir),
            new ApprovalCandidate("curl", null));

        Assert.Equal(2, grants.Count);
        Assert.All(grants, grant => Assert.Equal(GrantScope.Everywhere.Instance, grant.Scope));
    }

    [Theory]
    [InlineData(GrantScopeKind.Session)]
    [InlineData(GrantScopeKind.Folder)]
    [InlineData(GrantScopeKind.Everywhere)]
    public void Pure_side_effect_verbs_never_become_grants(GrantScopeKind kind)
    {
        // echo / printf / true / false are allowed for the current call but
        // never stored. The lookup skips them the same way.
        var grant = Assert.Single(Build(
            kind,
            new ApprovalCandidate("echo", null),
            new ApprovalCandidate("git status", ProjectDir)));

        Assert.Equal("git status", grant.Candidate.Verb);
    }

    [Fact]
    public void Structured_grants_keep_distinct_parser_tokens_with_one_legacy_projection()
    {
        var grants = Build(
            GrantScopeKind.Everywhere,
            new ApprovalCandidate("whoami", null)
            {
                Shell = ApprovalShell.Bash,
                VerbTokens = Array.AsReadOnly(["whoami", "user"]),
            },
            new ApprovalCandidate("whoami", null)
            {
                Shell = ApprovalShell.Bash,
                VerbTokens = Array.AsReadOnly(["whoami", "admin"]),
            });

        Assert.Collection(
            grants,
            first => Assert.Equal(["whoami", "user"], first.Candidate.VerbTokens),
            second => Assert.Equal(["whoami", "admin"], second.Candidate.VerbTokens));
    }

    [Fact]
    public void Repository_scope_requires_the_offered_repository()
    {
        Assert.Throws<InvalidOperationException>(() => GrantBuilder.Build(
            [new ApprovalCandidate("git status", ProjectDir)],
            GrantScopeKind.Repository,
            ProjectDir,
            SessionDir,
            repositoryCommonDirectory: null));
    }

    private static IReadOnlyList<ToolApprovalGrant> Build(
        GrantScopeKind kind,
        params ApprovalCandidate[] candidates)
        => GrantBuilder.Build(candidates, kind, SessionDir, SessionDir, repositoryCommonDirectory: null);
}
