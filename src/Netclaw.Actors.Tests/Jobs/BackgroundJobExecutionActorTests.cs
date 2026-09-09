// -----------------------------------------------------------------------
// <copyright file="BackgroundJobExecutionActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Jobs;

[Collection(BackgroundJobProcessCollection.Name)]
public class BackgroundJobExecutionActorTests : TestKit
{
    private static readonly ShellExecutionEnvironment ShellEnvironment = TestShellEnvironment.Current;
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

    private static string LongRunningCommand => TestShellEnvironment.LongRunningCommand;

    private BackgroundJobDefinition MakeDefinition(string command, int timeoutSeconds = 600) => new()
    {
        Id = new BackgroundJobId(Guid.NewGuid().ToString("N")[..12]),
        Command = command,
        ManagedTemporaryDirectory = Path.Combine(_dir.Path, "managed-temp"),
        ManagedTemporaryAuthorityRoot = _dir.Path,
        SessionId = new Netclaw.Actors.Protocol.SessionId("test/thread"),
        Rationale = "test",
        Status = BackgroundJobStatus.Running,
        StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        OriginChannelType = ChannelType.Tui,
        TimeoutSeconds = timeoutSeconds
    };

    private IActorRef SpawnExecution(
        BackgroundJobDefinition definition,
        IActorRef probe,
        ShellExecutionEnvironment? environment = null)
    {
        var outputPath = _store.GetOutputLogPath(definition.Id);
        var props = Props.Create(() => new BackgroundJobExecutionActor(
            definition,
            outputPath,
            TimeProvider.System,
            BackgroundShellLaunchFixture.Create(
                definition.Command, _dir.Path, definition.SessionId.Value, environment ?? ShellEnvironment, definition.WorkingDirectory)));
        return Sys.ActorOf(ForwardingParent.Props(props, probe), $"exec-{definition.Id}");
    }

    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "This test uses the native Bash TCP redirection and process identifier.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "Native Bash process ownership proof")]
    public async Task Stop_before_actor_adoption_reclaims_the_real_process()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var command = $"printf '%s\\n' $$ > /dev/tcp/127.0.0.1/{port}; sleep 120";
        var definition = MakeDefinition(command);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = TestToolExecutionContext.CreateBound("test/thread", _dir.Path,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        var launch = new ShellProcessLaunch(command, _dir.Path, context.Invocation,
            new ShellCommandPolicy(ShellEnvironment), new ToolPathPolicy(ShellEnvironment, []), async ct =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            });
        var actor = Sys.ActorOf(Props.Create(() => new BackgroundJobExecutionActor(
            definition, _store.GetOutputLogPath(definition.Id), TimeProvider.System, launch)));
        await WatchAsync(actor);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        // System messages precede user messages. Suspend prevents adoption but still permits Stop.
        ((IInternalActorRef)actor).Suspend();
        release.SetResult();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            using var reader = new StreamReader(connection.GetStream());
            var pid = int.Parse((await reader.ReadLineAsync(deadline.Token))!);
            using var process = Process.GetProcessById(pid);
            Assert.False(process.HasExited);
            Sys.Stop(actor);
            await ExpectTerminatedAsync(actor, cancellationToken: deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.HasExited);
        }
        finally
        {
            Sys.Stop(actor);
        }
    }

    [Fact]
    public async Task Missing_selected_executable_reports_exact_host_without_fallback()
    {
        const string missingExecutable = @"C:\missing\pwsh.exe";
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            missingExecutable,
            ShellSyntaxTree.PwshDialect.PowerShell7);
        var definition = MakeDefinition("Get-ChildItem");
        var probe = CreateTestProbe("parent");
        SpawnExecution(definition, probe, environment);

        var completed = await probe.ExpectMsgAsync<BackgroundJobCompleted>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(BackgroundJobStatus.Failed, completed.Status);
        Assert.Contains(missingExecutable, completed.OutputTail);
        Assert.DoesNotContain("powershell.exe", completed.OutputTail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", completed.OutputTail, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task RunningJob_OutputIsObservableOnDiskBeforeExit()
    {
        // The detached-process contract: a job that never exits (dev server)
        // must still have its output readable from the log while it runs.
        var command = ShellEnvironment.Grammar == ShellGrammar.PowerShell
            ? "Write-Output server-is-up; Start-Sleep -Seconds 300"
            : "echo server-is-up && sleep 300";
        var definition = MakeDefinition(command);
        var probe = CreateTestProbe("parent");
        var actor = SpawnExecution(definition, probe);
        var outputPath = _store.GetOutputLogPath(definition.Id);

        await AwaitAssertAsync(() =>
            {
                var (tail, _) = JobOutputLog.ReadTail(outputPath, 2000);
                Assert.Contains("server-is-up", tail);
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        // The process is still alive — no completion has been reported.
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100),
            cancellationToken: TestContext.Current.CancellationToken);

        actor.Tell(new CancelBackgroundJob(
            definition.Id, definition.SessionId, definition.Audience, definition.Boundary));
        var completed = await probe.ExpectMsgAsync<BackgroundJobCompleted>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
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
