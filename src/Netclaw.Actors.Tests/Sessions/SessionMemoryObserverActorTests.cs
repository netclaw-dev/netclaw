using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionMemoryObserverActorTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();

    public SessionMemoryObserverActorTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore();
    }

    [Fact]
    public async Task ReceiveTimeout_with_no_new_content_does_not_reply_to_parent()
    {
        var observer = Sys.ActorOf(
            SessionMemoryObserverActor.CreateProps(
                new SessionId("test-channel/observer-timeout"),
                _fakeChatClient,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5)));

        var parentProbe = CreateTestProbe("observer-parent");

        observer.Tell(ReceiveTimeout.Instance, parentProbe.Ref);

        await parentProbe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Explicit_distill_request_with_no_new_content_still_replies_empty()
    {
        var observer = Sys.ActorOf(
            SessionMemoryObserverActor.CreateProps(
                new SessionId("test-channel/observer-explicit"),
                _fakeChatClient,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5)));

        var parentProbe = CreateTestProbe("observer-explicit-parent");

        observer.Tell(new DistillMemories(), parentProbe.Ref);

        var reply = await parentProbe.ExpectMsgAsync<SessionDistillationCompleted>(TimeSpan.FromSeconds(3));
        Assert.Empty(reply.Proposals);
        Assert.Null(reply.FailureReason);
    }
}
