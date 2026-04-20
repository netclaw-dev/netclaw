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
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Session_approval_is_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, ct);
        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct);

        Assert.Empty(unapproved);
    }

    [Fact]
    public async Task Unapproved_pattern_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct);

        Assert.Equal(["git push"], unapproved);
    }

    [Fact]
    public async Task Per_audience_isolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, ct);

        Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct));
        Assert.Equal(["git push"], await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Team, new ToolName("shell_execute"), ["git push"], ct));
    }

    [Fact]
    public async Task Per_tool_isolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, ct);

        Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct));
        Assert.Equal(["git push"], await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("file_write"), ["git push"], ct));
    }

    [Fact]
    public async Task Single_token_approval_requires_exact_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["gh"], persistent: false, ct);

        // Single-token "gh" should NOT match "gh pr" — prevents approving
        // "gh --help" from also approving "gh pr create"
        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["gh pr"], ct);
        Assert.Equal(["gh pr"], unapproved);
    }

    [Fact]
    public async Task Multi_token_approval_prefix_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, ct);

        // Multi-token "git push" should match "git push origin" via prefix
        var unapproved = await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push origin"], ct);
        Assert.Empty(unapproved);
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

            await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: true, ct);

            Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct));

            var actor2 = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service2 = CreateService(actor2);
            Assert.Empty(await service2.GetUnapprovedPatternsAsync("different-session", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Case_insensitive_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["Git Push"], persistent: false, ct);
        Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct));
    }

    [Fact]
    public async Task Session_approvals_do_not_leak_across_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = Sys.ActorOf(ToolApprovalActor.CreateProps());
        var service = CreateService(actor);

        await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, ct);

        Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct));
        Assert.Equal(["git push"], await service.GetUnapprovedPatternsAsync("session-b", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct));
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

            await service.RecordApprovalAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], persistent: false, ct);

            Assert.Empty(await service.GetUnapprovedPatternsAsync("session-a", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct));

            var actor2 = Sys.ActorOf(ToolApprovalActor.CreateProps(store));
            var service2 = CreateService(actor2);
            Assert.Equal(["git push"], await service2.GetUnapprovedPatternsAsync("different-session", TrustAudience.Personal, new ToolName("shell_execute"), ["git push"], ct));
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
