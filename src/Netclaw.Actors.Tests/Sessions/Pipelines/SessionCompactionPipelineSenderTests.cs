// -----------------------------------------------------------------------
// <copyright file="SessionCompactionPipelineSenderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

public sealed class SessionCompactionPipelineSenderTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    public async Task Compaction_outputs_use_no_sender_inside_a_foreign_actor_context(string outcome)
    {
        var outputProbe = CreateTestProbe();
        var parameters = new CompactionParameters(
            new SessionId("compaction/sender-test"),
            PreCompactionInputTokens: 1,
            KeepRecentToolResults: 0,
            KeepRecentMessages: 2,
            ContextWindowTokens: 32_000,
            SidecarTimeout: TimeSpan.FromSeconds(1),
            CompactionTimeout: outcome == "success" ? TimeSpan.FromSeconds(1) : TimeSpan.Zero);
        var caller = Sys.ActorOf(Props.Create(() => new CompactionCaller(
            outputProbe.Ref,
            parameters,
            new FakeChatClient(),
            Logging.GetLogger(Sys, this))));

        caller.Tell(new RunCompaction());

        if (outcome == "success")
        {
            var completed = await outputProbe.ExpectMsgAsync<CompactionWorkCompleted>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, completed.OperationId);
        }
        else
        {
            var failed = await outputProbe.ExpectMsgAsync<CompactionWorkFailed>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.IsAssignableFrom<OperationCanceledException>(failed.Cause);
        }

        Assert.Equal(ActorRefs.NoSender, outputProbe.LastSender);
    }

    private sealed record RunCompaction;

    private sealed class CompactionCaller : ReceiveActor
    {
        public CompactionCaller(
            IActorRef output,
            CompactionParameters parameters,
            FakeChatClient client,
            ILoggingAdapter log)
        {
            Receive<RunCompaction>(run =>
            {
                _ = SessionCompactionPipeline.ExecuteAsync(
                    SessionState.Empty,
                    parameters,
                    client,
                    output,
                    log,
                    operationId: 1);
            });
        }
    }
}
