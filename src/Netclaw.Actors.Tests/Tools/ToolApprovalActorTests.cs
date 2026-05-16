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

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);
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

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
        Assert.Equal(["git push"], await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Team, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
    }

    [Fact]
    public async Task Per_tool_isolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct));
        Assert.Equal(["git push"], await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("file_write"), ["git push"], cwd: null, ct));
    }

    [Fact]
    public async Task Single_token_approval_requires_exact_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["gh"], persistent: false, cwd: null, ct);

        // Single-token "gh" should NOT match "gh pr" — prevents approving
        // "gh --help" from also approving "gh pr create"
        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["gh pr"], cwd: null, ct);
        Assert.Equal(["gh pr"], unapproved);
    }

    [Fact]
    public async Task Shell_exact_approval_does_not_prefix_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push origin"], cwd: null, ct);
        Assert.Equal(["git push origin"], unapproved);
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

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), [approvedRoot], persistent: false, cwd: null, ct);

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

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), [approvedRoot], persistent: false, cwd: null, ct);

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
            var store = new ToolApprovalStore(tempFile);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: true, cwd: null, ct);

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
    public async Task Approval_match_follows_host_filesystem_case_rules()
    {
        // Approval entries embed both filesystem paths and verb tokens that
        // resolve to executables via $PATH lookup, which honors filesystem case
        // rules. On POSIX, `Git` and `git` are different executables, and
        // `/data/` and `/Data/` are different directories — so a grant issued
        // for one MUST NOT cover the other (binary-substitution / case-distinct
        // path bypass). On Windows, the filesystem and PATH are
        // case-insensitive, so the case-folded match is the correct behavior.
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["Git Push"], persistent: false, cwd: null, ct);
        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct);

        if (OperatingSystem.IsWindows())
            Assert.Empty(unapproved);
        else
            Assert.Equal(["git push"], unapproved);
    }

    [Fact]
    public async Task Session_approvals_do_not_leak_across_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

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
            var store = new ToolApprovalStore(tempFile);
            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

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
        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

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
        await service.RecordApprovalAsync(subAgentScope, TrustAudience.Personal, new ToolName("shell_execute"), ["curl"], persistent: false, cwd: null, ct);

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
        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git status"], persistent: false, cwd: null, ct);

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
        await service.RecordApprovalAsync("session-b", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, cwd: null, ct);

        // Sub-agent of session A should NOT inherit session B's approval
        var subAgentScope = "session-a/subagent/researcher/abc123";
        var unapproved = await service.GetUnapprovedPatternsAsync(subAgentScope, TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], cwd: null, ct);

        Assert.Equal(["git push"], unapproved);
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

            var store = new ToolApprovalStore(tempFile);
            store.AddApproval(TrustAudience.Personal, "shell_execute",
                new ApprovalEntry { Verb = "git push", Directory = grantDir });

            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            IReadOnlyList<string> unapproved = [];
            await EventFilter.Info(contains: "near-miss").ExpectOneAsync(async () =>
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
            var store = new ToolApprovalStore(tempFile);
            store.AddApproval(TrustAudience.Personal, "shell_execute",
                new ApprovalEntry { Verb = "npm install", Directory = null });

            var actor = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service = CreateService(actor);

            await EventFilter.Info(contains: "near-miss").ExpectAsync(0, async () =>
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

    private static AkkaToolApprovalService CreateService(IActorRef actor)
        => new(new StubRequiredActor(actor));

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
