// -----------------------------------------------------------------------
// <copyright file="BackgroundJobExecutionActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Jobs;

public class BackgroundJobExecutionActorTests : TestKit
{
    private readonly DisposableTempDir _dir = new();
    private BackgroundJobDefinitionStore _store = null!;

    public BackgroundJobExecutionActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _store = new BackgroundJobDefinitionStore(paths);
    }

    protected override async Task AfterAllAsync()
    {
        _dir.Dispose();
        await base.AfterAllAsync();
    }

    private static string LongRunningCommand =>
        OperatingSystem.IsWindows() ? "ping -n 300 127.0.0.1" : "sleep 300";

    private BackgroundJobDefinition MakeDefinition(string command, int timeoutSeconds = 600) => new()
    {
        Id = new BackgroundJobId(Guid.NewGuid().ToString("N")[..12]),
        Command = command,
        SessionId = new Netclaw.Actors.Protocol.SessionId("test/thread"),
        Rationale = "test",
        Status = BackgroundJobStatus.Running,
        StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        OriginChannelType = ChannelType.Tui,
        TimeoutSeconds = timeoutSeconds
    };

    private IActorRef SpawnExecution(BackgroundJobDefinition definition, IActorRef probe)
    {
        var outputPath = _store.GetOutputLogPath(definition.Id);
        var props = Props.Create(() => new BackgroundJobExecutionActor(definition, outputPath, TimeProvider.System));
        return Sys.ActorOf(ForwardingParent.Props(props, probe), $"exec-{definition.Id}");
    }

    [Fact]
    public async Task SuccessfulCompletion_ReportsCompletedToParent()
    {
        var definition = MakeDefinition("echo hello-world");
        var probe = CreateTestProbe("parent");
        SpawnExecution(definition, probe);

        var completed = await probe.ExpectMsgAsync<BackgroundJobCompleted>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(definition.Id, completed.JobId);
        Assert.Equal(BackgroundJobStatus.Completed, completed.Status);
        Assert.Equal(0, completed.ExitCode);
        Assert.Contains("hello-world", completed.OutputTail ?? "");
    }

    [Fact]
    public async Task ProcessTimeout_KillsAndReportsTimedOut()
    {
        var definition = MakeDefinition(LongRunningCommand, timeoutSeconds: 1);
        var probe = CreateTestProbe("parent");
        SpawnExecution(definition, probe);

        var completed = await probe.ExpectMsgAsync<BackgroundJobCompleted>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(definition.Id, completed.JobId);
        Assert.Equal(BackgroundJobStatus.TimedOut, completed.Status);
    }

    [Fact]
    public async Task Cancellation_KillsAndReportsCancelled()
    {
        var definition = MakeDefinition(LongRunningCommand);
        var probe = CreateTestProbe("parent");
        var actor = SpawnExecution(definition, probe);

        actor.Tell(new CancelBackgroundJob(
            definition.Id,
            definition.SessionId,
            definition.Audience,
            definition.Boundary));

        var completed = await probe.ExpectMsgAsync<BackgroundJobCompleted>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(definition.Id, completed.JobId);
        Assert.Equal(BackgroundJobStatus.Cancelled, completed.Status);
    }

    /// <summary>
    /// Creates a child actor and forwards all messages from it to a probe,
    /// making the probe act as the logical parent for assertion purposes.
    /// </summary>
    private sealed class ForwardingParent : ReceiveActor
    {
        public static Props Props(Props childProps, IActorRef probe) =>
            Akka.Actor.Props.Create(() => new ForwardingParent(childProps, probe));

        public ForwardingParent(Props childProps, IActorRef probe)
        {
            var child = Context.ActorOf(childProps, "child");
            ReceiveAny(msg =>
            {
                if (Sender.Equals(child))
                    probe.Forward(msg);
                else
                    child.Forward(msg);
            });
        }
    }
}
