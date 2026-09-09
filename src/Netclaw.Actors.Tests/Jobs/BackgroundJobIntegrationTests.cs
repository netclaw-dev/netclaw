// -----------------------------------------------------------------------
// <copyright file="BackgroundJobIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Security;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Jobs;

/// <summary>
/// Integration tests exercising the full background job lifecycle:
/// submission → process execution → completion → DeliverTrustedSessionTurn
/// delivery via gateway resolution. Follows the same anchor pattern as
/// <see cref="Reminders.ReminderManagerActorTests.Mode_B_reminder_dispatches_to_resolved_gateway_and_completes_on_CommandAck"/>.
/// </summary>
[Collection(BackgroundJobProcessCollection.Name)]
public class BackgroundJobIntegrationTests : TestKit
{
    private readonly DisposableTempDir _dir = new();
    private BackgroundJobDefinitionStore _store = null!;

    public BackgroundJobIntegrationTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _store = new BackgroundJobDefinitionStore(paths);

        builder.StartActors((system, registry, _) =>
        {
            var manager = system.ActorOf(
                Props.Create(() => new BackgroundJobManagerActor(
                    _store,
                    TimeProvider.System,
                    TestShellEnvironment.Current)),
                "background-job-manager");
            registry.Register<BackgroundJobManagerActorKey>(manager);
        });
    }

    protected override async Task AfterAllAsync()
    {
        _dir.Dispose();
        await base.AfterAllAsync();
    }

    private IActorRef GetManager() => ActorRegistry.For(Sys).Get<BackgroundJobManagerActorKey>();

    private StartBackgroundJob MakeStartCommand(string command, ChannelType channelType = ChannelType.Slack, string? workingDirectory = null) => new()
    {
        Launch = BackgroundShellLaunchFixture.Create(command, _dir.Path, "C0123ABC/1712000000.000001", TestShellEnvironment.Current, workingDirectory),
        Rationale = "integration test",
        OriginChannelType = channelType,
        TimeoutSeconds = 30
    };

    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "This test uses native Bash TCP redirection and a process identifier.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "Native Bash detached process proof")]
    public async Task Dispatcher_launch_survives_submission_cancellation_and_the_manager_can_stop_it()
    {
        // The original manager constructor must accept the canonical identity carried by a checked launch.
        var manager = Sys.ActorOf(Props.Create(() => new BackgroundJobManagerActor(_store, TimeProvider.System)));
        await manager.Ask<BackgroundJobManagerHealthResponse>(GetBackgroundJobManagerHealth.Instance,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var command = $"printf '%s\\n' $$ > /dev/tcp/127.0.0.1/{port}; sleep 120";
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode> { [ShellTool.ToolName] = ToolApprovalMode.Auto }
        };
        var commandPolicy = new ShellCommandPolicy(TestShellEnvironment.Current);
        var pathPolicy = new ToolPathPolicy(TestShellEnvironment.Current, []);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(TestToolAccessPolicy.Create(config, commandPolicy, pathPolicy));
        var policy = TestToolAccessPolicy.Create(config, commandPolicy, pathPolicy);
        var executor = new DispatchingToolExecutor(registry, policy);
        var context = TestToolExecutionContext.CreateBound("launch/detached", _dir.Path,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal });
        using var submissionCancellation = new CancellationTokenSource();
        var launch = await executor.PrepareShellLaunchAsync(
            new FunctionCallContent("detached", ShellTool.ToolName, ToolInput.Create("Command", command)),
            context, submissionCancellation.Token);
        var request = new StartBackgroundJob
        {
            Launch = launch,
            Rationale = "Verify detached lifetime.",
            OriginChannelType = ChannelType.Tui
        };
        var accepted = await manager.Ask<BackgroundJobStarted>(request, TimeSpan.FromSeconds(10), submissionCancellation.Token);
        submissionCancellation.Cancel();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            using var reader = new StreamReader(connection.GetStream());
            using var process = Process.GetProcessById(int.Parse((await reader.ReadLineAsync(deadline.Token))!));
            Assert.False(process.HasExited);
            var status = await manager.Ask<BackgroundJobStatusResponse>(
                new QueryBackgroundJob(accepted.JobId, request.SessionId, request.Audience, request.Boundary),
                TimeSpan.FromSeconds(10), deadline.Token);
            Assert.Equal(BackgroundJobStatus.Running, status.Status);
            await manager.Ask<BackgroundJobCancelResponse>(
                new CancelBackgroundJob(accepted.JobId, request.SessionId, request.Audience, request.Boundary),
                TimeSpan.FromSeconds(10), deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.HasExited);
        }
        finally
        {
            Sys.Stop(manager);
        }
    }

    [Fact]
    public async Task BackgroundJob_Completes_And_DeliversResult_ViaGateway()
    {
        var manager = GetManager();

        var gatewayProbe = CreateTestProbe("fake-slack-gateway");
        var autoAckRef = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-slack-gateway-completion");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

        var started = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand("echo integration-test-output"),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.NotNull(started.JobId.Value);

        var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("C0123ABC/1712000000.000001", delivered.SessionId.Value);
        Assert.Contains("integration-test-output", delivered.Content);
        Assert.Contains("completed", delivered.Content.ToLowerInvariant());
        Assert.Equal(ChannelType.Slack, delivered.Source.ChannelType);
        Assert.Equal(TrustAudience.Personal, delivered.Source.Audience);
        Assert.Equal(TrustBoundary.Personal, delivered.Source.Boundary);
        Assert.Equal(PrincipalClassification.VerifiedAutomation, delivered.Source.Principal);
        Assert.Equal("background-job", delivered.Source.Provenance.SourceKind?.Value);
        Assert.NotNull(delivered.Source.BackgroundJobId);
        Assert.StartsWith("bg-job:", delivered.Source.BackgroundJobId!.Value.Value);

        await AwaitAssertAsync(() =>
        {
            var def = _store.Get(started.JobId);
            Assert.NotNull(def);
            Assert.Equal(BackgroundJobStatus.Completed, def!.Status);
            Assert.NotNull(def.CompletedAtMs);
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BackgroundJob_WithMissingWorkingDirectory_FailsWithHelpfulError()
    {
        // #1286: a non-existent working directory must fail loudly with the mkdir remedy
        // instead of an opaque "Failed to start: ..." from Process.Start.
        var manager = GetManager();

        var gatewayProbe = CreateTestProbe("fake-slack-gateway-missing-cwd");
        var autoAckRef = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-slack-gateway-missing-cwd");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

        var missingDir = Path.Combine(_dir.Path, "does", "not", "exist");

        var started = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand("echo hi", workingDirectory: missingDir),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("does not exist", delivered.Content);
        Assert.Contains(TestShellEnvironment.CreateDirectoryCommandName, delivered.Content);
        Assert.Contains("failed", delivered.Content.ToLowerInvariant());

        await AwaitAssertAsync(() =>
        {
            var def = _store.Get(started.JobId);
            Assert.NotNull(def);
            Assert.Equal(BackgroundJobStatus.Failed, def!.Status);
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BackgroundJob_DeliversResult_ToCorrectGateway_ForRehydration()
    {
        var manager = GetManager();

        var gatewayProbe = CreateTestProbe("fake-slack-gateway-rehydrate");
        var autoAckRef = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-slack-gateway-rehydrate");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

        var started = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand("echo rehydration-test"),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("C0123ABC/1712000000.000001", delivered.SessionId.Value);
        Assert.Contains("rehydration-test", delivered.Content);
        Assert.Equal(ChannelType.Slack, delivered.Source.ChannelType);
        Assert.Contains("echo rehydration-test", delivered.Content);
    }

    [Fact]
    public async Task CancelRunningJob_ViaCheckBackgroundJobTool()
    {
        var manager = GetManager();

        var gatewayProbe = CreateTestProbe("fake-slack-gateway-cancel");
        var autoAckRef = Sys.ActorOf(
            Props.Create(() => new AutoAckTrustedGateway(gatewayProbe.Ref)),
            "auto-ack-slack-gateway-cancel");
        ActorRegistry.For(Sys).Register<SlackGatewayActorKey>(autoAckRef);

        var started = await manager.Ask<BackgroundJobStarted>(
            MakeStartCommand(TestShellEnvironment.LongRunningCommand),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var tool = new CheckBackgroundJobTool(manager);
        var cancelArgs = new Dictionary<string, object?>
        {
            ["JobId"] = started.JobId.Value,
            ["Cancel"] = true
        };
        var context = TestToolExecutionContext.CreateBound("C0123ABC/1712000000.000001", "/tmp", new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal
        });

        var cancelResult = await tool.ExecuteAsync(cancelArgs, context, TestContext.Current.CancellationToken);
        Assert.Contains("Cancellation request sent", cancelResult);
        Assert.Contains(started.JobId.Value, cancelResult);

        var delivered = await gatewayProbe.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("C0123ABC/1712000000.000001", delivered.SessionId.Value);
        Assert.Contains("cancelled", delivered.Content.ToLowerInvariant());

        await AwaitAssertAsync(() =>
        {
            var def = _store.Get(started.JobId);
            Assert.NotNull(def);
            Assert.Equal(BackgroundJobStatus.Cancelled, def!.Status);
            return Task.CompletedTask;
        }, duration: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class AutoAckTrustedGateway : ReceiveActor
    {
        public AutoAckTrustedGateway(IActorRef probe)
        {
            Receive<DeliverTrustedSessionTurn>(msg =>
            {
                probe.Tell(msg);
                Sender.Tell(CommandAck.For(msg.SessionId));
            });
        }
    }
}
