// -----------------------------------------------------------------------
// <copyright file="BackgroundJobIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

namespace Netclaw.Actors.Tests.Jobs;

/// <summary>
/// Integration tests exercising the full background job lifecycle:
/// submission → process execution → completion → DeliverTrustedSessionTurn
/// delivery via gateway resolution. Follows the same anchor pattern as
/// <see cref="Reminders.ReminderManagerActorTests.Mode_B_reminder_dispatches_to_resolved_gateway_and_completes_on_CommandAck"/>.
/// </summary>
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
                Props.Create(() => new BackgroundJobManagerActor(_store, TimeProvider.System)),
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
        Command = command,
        WorkingDirectory = workingDirectory,
        SessionId = new SessionId("C0123ABC/1712000000.000001"),
        Rationale = "integration test",
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        OriginChannelType = channelType,
        TimeoutSeconds = 30
    };

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
        Assert.StartsWith("bg-job:", delivered.Source.BackgroundJobId);

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
        Assert.Contains("mkdir", delivered.Content);
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
            MakeStartCommand("sleep 300"),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var tool = new CheckBackgroundJobTool(manager);
        var cancelArgs = new Dictionary<string, object?>
        {
            ["JobId"] = started.JobId.Value,
            ["Cancel"] = true
        };
        var context = new ToolExecutionContext("C0123ABC/1712000000.000001", "/tmp")
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal
        };

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
