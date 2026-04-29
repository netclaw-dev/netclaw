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
using Xunit;

namespace Netclaw.Actors.Tests.Jobs;

public class BackgroundJobExecutionActorTests : TestKit
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-exec-{Guid.NewGuid():N}");
    private BackgroundJobDefinitionStore _store = null!;

    public BackgroundJobExecutionActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var paths = new NetclawPaths(_tempDir);
        paths.EnsureDirectoriesExist();
        _store = new BackgroundJobDefinitionStore(paths);
    }

    private BackgroundJobDefinition MakeDefinition(string command, int timeoutSeconds = 600) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Command = command,
        SessionId = "test/thread",
        Rationale = "test",
        Status = BackgroundJobStatus.Running,
        StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Audience = TrustAudience.Personal,
        Boundary = SecurityPolicyDefaults.PersonalBoundary,
        OriginChannelType = ChannelType.Tui,
        TimeoutSeconds = timeoutSeconds
    };

    private IActorRef SpawnExecution(BackgroundJobDefinition definition, IActorRef probe)
    {
        var outputPath = _store.GetOutputLogPath(new BackgroundJobId(definition.Id));
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

        Assert.Equal(new BackgroundJobId(definition.Id), completed.JobId);
        Assert.Equal(BackgroundJobStatus.Completed, completed.Status);
        Assert.Equal(0, completed.ExitCode);
        Assert.Contains("hello-world", completed.OutputTail ?? "");
    }

    [Fact]
    public async Task ProcessTimeout_KillsAndReportsTimedOut()
    {
        var definition = MakeDefinition("sleep 300", timeoutSeconds: 1);
        var probe = CreateTestProbe("parent");
        SpawnExecution(definition, probe);

        var completed = await probe.ExpectMsgAsync<BackgroundJobCompleted>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new BackgroundJobId(definition.Id), completed.JobId);
        Assert.Equal(BackgroundJobStatus.TimedOut, completed.Status);
    }

    [Fact]
    public async Task Cancellation_KillsAndReportsCancelled()
    {
        var definition = MakeDefinition("sleep 300");
        var probe = CreateTestProbe("parent");
        var actor = SpawnExecution(definition, probe);

        actor.Tell(new CancelBackgroundJob(
            new BackgroundJobId(definition.Id),
            new Netclaw.Actors.Protocol.SessionId(definition.SessionId),
            definition.Audience,
            definition.Boundary));

        var completed = await probe.ExpectMsgAsync<BackgroundJobCompleted>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new BackgroundJobId(definition.Id), completed.JobId);
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
