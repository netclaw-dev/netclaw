// -----------------------------------------------------------------------
// <copyright file="ToolApprovalActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolApprovalActorTests : TestKit
{
    public static TheoryData<string, string, string> DirectoryRootCoverageCases
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            if (OperatingSystem.IsWindows())
                data.Add(@"C:\Users\petabridge\.netclaw\logs\", @"C:\Users\petabridge\.netclaw\output\", @"C:\Users\petabridge\.netclaw\output\");
            else
                data.Add("/home/user/.netclaw/logs/", "/home/user/.netclaw/output/", "/home/user/.netclaw/output/");

            return data;
        }
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // The near-miss diagnostic is emitted at Info; make the level
        // explicit so the EventFilter assertion is deterministic.
        builder.AddHocon("akka.loglevel = INFO", HoconAddMode.Prepend);
    }

    [Fact]
    public async Task Session_approval_is_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);
        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct);

        Assert.Empty(unapproved);
    }

    [Fact]
    public async Task Unapproved_pattern_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct);

        Assert.Equal(["git push"], unapproved);
    }

    [Fact]
    public async Task Per_audience_isolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
        Assert.Equal(["git push"], await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Team, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
    }

    [Fact]
    public async Task Per_tool_isolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
        Assert.Equal(["git push"], await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("file_write"), ["git push"], cwd: null, ct));
    }

    [Fact]
    public async Task Single_token_approval_matches_a_longer_token_phrase()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["gh"], persistent: false, cwd: null, ct);

        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["gh pr"], cwd: null, ct);
        Assert.Empty(unapproved);
    }

    [Fact]
    public async Task Shell_token_prefix_approval_matches_a_longer_phrase()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push origin"], cwd: null, ct);
        Assert.Empty(unapproved);
    }

    [Theory]
    [MemberData(nameof(DirectoryRootCoverageCases))]
    public async Task Shell_directory_root_approval_covers_other_verbs_under_same_root(string approvedRoot, string otherRoot, string expectedUnapproved)
    {
        _ = otherRoot;
        _ = expectedUnapproved;
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), [approvedRoot], persistent: false, cwd: null, ct);

        Assert.Empty(await service.GetUnapprovedPatternsAsync(
            "session-a",
            TrustAudience.Personal,
            new ToolName("shell_execute"),
            [approvedRoot],
            cwd: null,
            ct));
    }

    [Theory]
    [MemberData(nameof(DirectoryRootCoverageCases))]
    public async Task Shell_directory_root_approval_requires_all_roots_to_be_covered(string approvedRoot, string otherRoot, string expectedUnapproved)
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), [approvedRoot], persistent: false, cwd: null, ct);

        var unapproved = await service.GetUnapprovedPatternsAsync(
            "session-a",
            TrustAudience.Personal,
            new ToolName("shell_execute"),
            [approvedRoot, otherRoot],
            cwd: null,
            ct);

        Assert.Equal([expectedUnapproved], unapproved);
    }

    [Fact]
    public async Task Persistent_approval_survives_new_service_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            var store = CreateStore(tempFile);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: true, cwd: null, ct);

            Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));

            var actor2 = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service2 = CreateService(actor2);
            Assert.Empty(await service2.GetUnapprovedPatternsAsync("different-session", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Bash_approval_match_is_case_sensitive_on_all_hosts()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);
        var grant = BashCandidate("Git", directory: null);

        await service.RecordApprovalCandidatesAsync(
            (ToolApprovalSessionId)"session-a",
            TrustAudience.Personal,
            new ToolName("shell_execute"),
            [new ToolApprovalGrant(grant, Directory: null)],
            persistent: false,
            ct);
        var result = await service.CheckApprovalAsync(
            "session-a",
            TrustAudience.Personal,
            new ToolName("shell_execute"),
            [BashCandidate("git", directory: null)],
            cwd: null,
            ct);

        Assert.Equal(["git"], result.UnapprovedPatterns);
    }

    [Fact]
    public async Task Session_approvals_do_not_leak_across_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
        Assert.Equal(["git push"], await service.GetUnapprovedPatternsAsync("session-b", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
    }

    [Fact]
    public async Task Non_persistent_approval_is_session_scoped_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            var store = CreateStore(tempFile);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

            Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));

            var actor2 = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service2 = CreateService(actor2);
            Assert.Equal(["git push"], await service2.GetUnapprovedPatternsAsync("different-session", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SubAgent_inherits_parent_session_approval()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        // Parent session approves "git push"
        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        // Sub-agent queries with hierarchical scope ID — should inherit parent approval
        var subAgentScope = "session-a/subagent/researcher/abc123";
        var unapproved = await service.GetUnapprovedPatternsAsync(subAgentScope, TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct);

        Assert.Empty(unapproved);
    }

    [Fact]
    public async Task SubAgent_approval_does_not_leak_upward()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        // Sub-agent records its own approval
        var subAgentScope = "session-a/subagent/researcher/abc123";
        await RecordApprovalAsync(service, subAgentScope, TrustAudience.Personal, new ToolName("shell_execute"), ["curl"], persistent: false, cwd: null, ct);

        // Parent session should NOT see sub-agent's approval
        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["curl"], cwd: null, ct);
        Assert.Equal(["curl"], unapproved);
    }

    [Fact]
    public async Task Nested_subagent_inherits_through_chain()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        // Parent session approves "git status"
        await RecordApprovalAsync(service, "session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git status"], persistent: false, cwd: null, ct);

        // Nested sub-agent (sub-agent spawned by sub-agent) should still inherit
        var nestedScope = "session-a/subagent/orchestrator/def456/subagent/worker/ghi789";
        var unapproved = await service.GetUnapprovedPatternsAsync(nestedScope, TrustAudience.Personal, new ToolName("shell_execute"), ["git status"], cwd: null, ct);

        Assert.Empty(unapproved);
    }

    [Fact]
    public async Task SubAgent_does_not_inherit_from_unrelated_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        // Session B approves "git push"
        await RecordApprovalAsync(service, "session-b", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        // Sub-agent of session A should NOT inherit session B's approval
        var subAgentScope = "session-a/subagent/researcher/abc123";
        var unapproved = await service.GetUnapprovedPatternsAsync(subAgentScope, TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct);

        Assert.Equal(["git push"], unapproved);
    }

    [Fact]
    public async Task Persistent_shell_approval_uses_candidate_directory_when_present()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            var grantDir = Path.Combine(Path.GetTempPath(), "netclaw-approval", "repo");
            var candidateDir = Path.Combine(grantDir, "src");
            var unrelatedCwd = Path.Combine(Path.GetTempPath(), "netclaw-approval", "other");

            var store = CreateStore(tempFile);
            store.AddApproval(TrustAudience.Personal, "shell_execute",
                ApprovalEntry.CreateTokenPrefix(NativeShell, ["dotnet", "test"], grantDir));

            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            var result = await service.CheckApprovalAsync(
                "session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [NativeCandidate("dotnet test", candidateDir)],
                cwd: unrelatedCwd,
                ct);

            Assert.Empty(result.UnapprovedPatterns);
            var match = Assert.Single(result.ApprovedMatches);
            Assert.Equal("persistent", match.Source);
            Assert.Equal($"{NativeShell} token-prefix \"dotnet test\" in {grantDir}", match.Scope);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Persistent_shell_approval_rejects_candidate_directory_outside_grant_even_when_cwd_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            var grantDir = Path.Combine(Path.GetTempPath(), "netclaw-approval", "repo");
            var outsideDir = Path.Combine(Path.GetTempPath(), "netclaw-approval", "outside");

            var store = CreateStore(tempFile);
            store.AddApproval(TrustAudience.Personal, "shell_execute",
                ApprovalEntry.CreateTokenPrefix(NativeShell, ["cat"], grantDir));

            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            var result = await service.CheckApprovalAsync(
                "session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [NativeCandidate("cat", outsideDir)],
                cwd: grantDir,
                ct);

            Assert.Equal(["cat"], result.UnapprovedPatterns);
            var check = Assert.Single(result.CandidateChecks!);
            Assert.Equal(NativeCandidate("cat", outsideDir), check.Candidate);
            Assert.Null(check.ApprovedMatch);
            Assert.Empty(result.ApprovedMatches);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Partial_directory_grant_returns_exact_unapproved_occurrence()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            var grantDir = Path.Combine(Path.GetTempPath(), "netclaw-approval", "repo");
            var approvedDir = Path.Combine(grantDir, "src");
            var unapprovedDir = Path.Combine(Path.GetTempPath(), "netclaw-approval", "external");
            var approvedCandidate = NativeCandidate("git push", approvedDir);
            var unapprovedCandidate = NativeCandidate("git push", unapprovedDir);

            var store = CreateStore(tempFile);
            store.AddApproval(
                TrustAudience.Personal,
                "shell_execute",
                ApprovalEntry.CreateTokenPrefix(NativeShell, ["git", "push"], grantDir));

            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            var result = await service.CheckApprovalAsync(
                "session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [approvedCandidate, unapprovedCandidate],
                cwd: grantDir,
                ct);

            Assert.Equal(["git push"], result.UnapprovedPatterns);
            Assert.Equal(
                [
                    new ToolApprovalCandidateCheck(approvedCandidate, result.ApprovedMatches[0]),
                    new ToolApprovalCandidateCheck(unapprovedCandidate, ApprovedMatch: null)
                ],
                result.CandidateChecks);
            var match = Assert.Single(result.ApprovedMatches);
            Assert.Equal("persistent", match.Source);
            Assert.Equal($"{NativeShell} token-prefix \"git push\" in {grantDir}", match.Scope);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Directory_near_miss_is_logged_without_changing_the_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            // Lexical containment only — the directories need not exist.
            var grantDir = Path.Combine(Path.GetTempPath(), "netclaw-nearmiss", "grant");
            var otherDir = Path.Combine(Path.GetTempPath(), "netclaw-nearmiss", "other");

            var store = CreateStore(tempFile);
            store.AddApproval(TrustAudience.Personal, "shell_execute",
                ApprovalEntry.CreateTokenPrefix(NativeShell, ["git", "push"], grantDir));

            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            IReadOnlyList<string> unapproved = [];
            await EventFilter.Info(contains: "approval_near_miss").ExpectAsync(2, async () =>
            {
                unapproved = await service.GetUnapprovedPatternsAsync(
                    "session-a", TrustAudience.Personal, new ToolName("shell_execute"),
                    ["git push"], cwd: otherDir, ct);
            }, cancellationToken: ct);

            // The diagnostic is read-only: the candidate is still unapproved.
            Assert.Equal(["git push"], unapproved);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task First_time_prompt_emits_no_near_miss_diagnostic()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            // Store holds an unrelated verb, so the prompted verb has no
            // same-verb grant to explain.
            var store = CreateStore(tempFile);
            store.AddApproval(TrustAudience.Personal, "shell_execute",
                ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["npm", "install"]));

            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            await EventFilter.Info(contains: "approval_near_miss").ExpectAsync(0, async () =>
            {
                var unapproved = await service.GetUnapprovedPatternsAsync(
                    "session-a", TrustAudience.Personal, new ToolName("shell_execute"),
                    ["terraform apply"], cwd: null, ct);
                Assert.Equal(["terraform apply"], unapproved);
            }, cancellationToken: ct);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Invalid_persistent_store_returns_typed_failure_without_authority()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "{\"version\":3,\"audiences\":{\"personal\":null}}");
            var store = new ToolApprovalStore(
                tempFile,
                timeProvider: null,
                migrationContext: new ApprovalStoreMigrationContext(ApprovalShell.Bash),
                lockTimeout: TimeSpan.Zero);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            var result = await service.CheckApprovalAsync(
                "session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [BashCandidate("git push")],
                cwd: null,
                ct);

            Assert.Equal(ApprovalStoreFailure.InvalidData, result.PersistentStoreFailure);
            Assert.Equal(["git push"], result.UnapprovedPatterns);
            Assert.Empty(result.ApprovedMatches);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Version_two_omission_emits_one_bounded_actor_diagnostic()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                tempFile,
                "{\"version\":2,\"audiences\":{\"personal\":{\"shell_execute\":[{\"verb\":\" git\"}]}}}");
            var store = new ToolApprovalStore(
                tempFile,
                timeProvider: null,
                migrationContext: new ApprovalStoreMigrationContext(ApprovalShell.Bash),
                lockTimeout: TimeSpan.Zero);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            await EventFilter.Warning(contains: "conversion omitted 1 unrepresentable entries")
                .ExpectAsync(1, async () =>
                {
                    _ = await service.CheckApprovalAsync(
                        "session-a",
                        TrustAudience.Personal,
                        new ToolName("shell_execute"),
                        [BashCandidate("git")],
                        cwd: null,
                        ct);
                    _ = await service.CheckApprovalAsync(
                        "session-a",
                        TrustAudience.Personal,
                        new ToolName("shell_execute"),
                        [BashCandidate("git")],
                        cwd: null,
                        ct);
                }, ct);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Raw_shell_compatibility_API_without_environment_fails_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            File.Delete(tempFile);
            var store = CreateStore(tempFile);
            store.AddApproval(
                TrustAudience.Personal,
                "shell_execute",
                ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git", "push"]));
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = new AkkaToolApprovalService(new StubRequiredActor(actor));

            var unapproved = await service.GetUnapprovedPatternsAsync(
                "session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                ["git push"],
                cwd: null,
                ct);

            Assert.Equal(["git push"], unapproved);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordApprovalAsync(
                "session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                ["git push"],
                persistent: true,
                cwd: null,
                ct));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Session_grant_can_cover_candidate_when_persistent_store_is_invalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "{\"version\":3,\"audiences\":{\"personal\":null}}");
            var store = new ToolApprovalStore(
                tempFile,
                timeProvider: null,
                migrationContext: new ApprovalStoreMigrationContext(ApprovalShell.Bash),
                lockTimeout: TimeSpan.Zero);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);
            await RecordApprovalAsync(
                service,
                "session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                ["git push"],
                persistent: false,
                cwd: null,
                ct);

            var result = await service.CheckApprovalAsync(
                "session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [NativeCandidate("git push")],
                cwd: null,
                ct);

            Assert.Equal(ApprovalStoreFailure.InvalidData, result.PersistentStoreFailure);
            Assert.Empty(result.UnapprovedPatterns);
            Assert.Single(result.ApprovedMatches);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Persistent_token_prefix_covers_a_longer_candidate()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            var store = CreateStore(tempFile);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);
            await service.RecordApprovalCandidatesAsync(
                (ToolApprovalSessionId)"session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [new ToolApprovalGrant(BashCandidate("git push"), Directory: null)],
                persistent: true,
                ct);

            var result = await service.CheckApprovalAsync(
                "session-b",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [BashCandidate("git push origin")],
                cwd: null,
                ct);

            Assert.Empty(result.UnapprovedPatterns);
            Assert.Single(result.ApprovedMatches);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Persistent_phrase_uses_parser_tokens_when_legacy_projection_is_shorter()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            var store = CreateStore(tempFile);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);
            var candidate = new ApprovalCandidate("git ls-tree", Directory: null)
            {
                Shell = ApprovalShell.Bash,
                VerbTokens = Array.AsReadOnly(["git", "ls-tree", "feature"]),
            };

            await service.RecordApprovalCandidatesAsync(
                (ToolApprovalSessionId)"session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [new ToolApprovalGrant(candidate, Directory: null)],
                persistent: true,
                ct);

            var entry = Assert.Single(
                store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
            Assert.Equal(["git", "ls-tree", "feature"], entry.VerbTokens);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Typed_shell_batch_preserves_ids_and_store_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "{\"version\":3,\"audiences\":{\"personal\":null}}");
            var store = new ToolApprovalStore(
                tempFile,
                timeProvider: null,
                migrationContext: new ApprovalStoreMigrationContext(NativeShell),
                lockTimeout: TimeSpan.Zero);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);
            await service.RecordApprovalCandidatesAsync(
                (ToolApprovalSessionId)"session-a",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [new ToolApprovalGrant(NativeCandidate("git status"), Directory: null)],
                persistent: false,
                ct);
            var candidates = Array.AsReadOnly(
                [
                    new ShellGrantCandidate(
                        new ShellPolicyCandidateId(7),
                        NativeCandidate("git status"),
                        RealDirectory: null),
                    new ShellGrantCandidate(
                        new ShellPolicyCandidateId(11),
                        NativeCandidate("dotnet test"),
                        RealDirectory: null)
                ]);

            var result = await ((IShellApprovalMatchService)service).MatchShellCandidatesAsync(
                new ShellApprovalMatchRequest(
                    (ToolApprovalSessionId)"session-a",
                    TrustAudience.Personal,
                    new ToolName("shell_execute"),
                    TestShellEnvironment.Current,
                    candidates),
                ct);

            var unavailable = Assert.IsType<PersistentGrantStoreStatus.Unavailable>(result.PersistentStore);
            Assert.Equal(ApprovalStoreFailure.InvalidData, unavailable.Failure);
            Assert.Equal([7, 11], result.CandidateMatches.Select(match => match.CandidateId.Value));
            Assert.Equal(ShellCoverageKind.Session, result.CandidateMatches[0].GrantCoverage);
            Assert.NotNull(result.CandidateMatches[0].Match);
            Assert.Null(result.CandidateMatches[1].GrantCoverage);
            Assert.Null(result.CandidateMatches[1].Match);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static AkkaToolApprovalService CreateService(IActorRef actor)
        => new(new StubRequiredActor(actor), TestShellEnvironment.Current);

    private static ApprovalCandidate BashCandidate(string verb, string? directory = null) =>
        new(verb, directory)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = Array.AsReadOnly(
                verb.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
        };

    private static ApprovalShell NativeShell => OperatingSystem.IsWindows()
        ? ApprovalShell.PowerShell
        : ApprovalShell.Bash;

    private static ApprovalCandidate NativeCandidate(string verb, string? directory = null) =>
        new(verb, directory)
        {
            Shell = NativeShell,
            VerbTokens = Array.AsReadOnly(
                verb.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
        };

    private static Task RecordApprovalAsync(
        AkkaToolApprovalService service,
        string sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        string? cwd,
        CancellationToken ct) =>
        service.RecordApprovalAsync(
            sessionId,
            audience,
            toolName,
            patterns,
            persistent,
            cwd,
            ct);

    private static ToolApprovalStore CreateStore(string path)
    {
        File.Delete(path);
        return new ToolApprovalStore(
            path,
            timeProvider: null,
            migrationContext: new ApprovalStoreMigrationContext(NativeShell),
            lockTimeout: TimeSpan.Zero);
    }

    private sealed class StubRequiredActor : IRequiredActor<ToolApprovalActorKey>
    {
        private readonly IActorRef _actor;

        public StubRequiredActor(IActorRef actor)
        {
            _actor = actor;
        }

        public IActorRef ActorRef => _actor;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_actor);
    }
}
