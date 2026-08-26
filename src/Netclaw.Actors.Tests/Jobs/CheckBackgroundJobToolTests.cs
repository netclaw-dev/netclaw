// -----------------------------------------------------------------------
// <copyright file="CheckBackgroundJobToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Jobs;

public sealed class CheckBackgroundJobToolTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    private ToolExecutionContext MakeContext(string sessionId = "test/thread") => TestToolExecutionContext.CreateBound(sessionId, "/tmp", new TestToolExecutionContextOptions
        {
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal
    });

    [Fact]
    public async Task StatusQuery_ReturnsCorrectState()
    {
        var fakeManager = Sys.ActorOf(Props.Create(() => new FakeQueryManager(
            new BackgroundJobStatusResponse
            {
                JobId = new BackgroundJobId("abc123"),
                Status = BackgroundJobStatus.Running,
                Found = true,
                Elapsed = TimeSpan.FromSeconds(42),
                Rationale = "build test"
            })));

        var tool = new CheckBackgroundJobTool(fakeManager);
        var args = new Dictionary<string, object?> { ["JobId"] = "abc123" };
        var result = await tool.ExecuteAsync(args, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Contains("abc123", result);
        Assert.Contains("running", result);
        Assert.Contains("42.0s", result);
        Assert.Contains("build test", result);
    }

    [Fact]
    public async Task Cancel_SendsCancellationAndReturnsConfirmation()
    {
        var probe = CreateTestProbe("cancel-probe");
        var fakeManager = Sys.ActorOf(Props.Create(() => new FakeCancelManager(probe.Ref)));

        var tool = new CheckBackgroundJobTool(fakeManager);
        var args = new Dictionary<string, object?> { ["JobId"] = "abc123", ["Cancel"] = true };
        var result = await tool.ExecuteAsync(args, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Contains("Cancellation request sent", result);
        Assert.Contains("abc123", result);

        var cancelled = await probe.ExpectMsgAsync<CancelBackgroundJob>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("abc123", cancelled.JobId.Value);
    }

    [Fact]
    public async Task NonExistentJob_ReturnsError()
    {
        var fakeManager = Sys.ActorOf(Props.Create(() => new FakeQueryManager(
            new BackgroundJobStatusResponse
            {
                JobId = new BackgroundJobId("missing"),
                Status = BackgroundJobStatus.Lost,
                Found = false
            })));

        var tool = new CheckBackgroundJobTool(fakeManager);
        var args = new Dictionary<string, object?> { ["JobId"] = "missing" };
        var result = await tool.ExecuteAsync(args, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Cancel_InaccessibleJob_ReturnsError()
    {
        var fakeManager = Sys.ActorOf(Props.Create(() => new FakeCancelManager(ActorRefs.Nobody, found: false)));

        var tool = new CheckBackgroundJobTool(fakeManager);
        var args = new Dictionary<string, object?> { ["JobId"] = "abc123", ["Cancel"] = true };
        var result = await tool.ExecuteAsync(args, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Contains("not found", result);
    }

    private sealed class FakeQueryManager : ReceiveActor
    {
        public FakeQueryManager(BackgroundJobStatusResponse response)
        {
            Receive<QueryBackgroundJob>(_ => Sender.Tell(response));
        }
    }

    private sealed class FakeCancelManager : ReceiveActor
    {
        public FakeCancelManager(IActorRef probe, bool found = true)
        {
            Receive<CancelBackgroundJob>(cmd =>
            {
                probe.Forward(cmd);
                Sender.Tell(new BackgroundJobCancelResponse(cmd.JobId, found));
            });
        }
    }
}
