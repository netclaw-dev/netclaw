// -----------------------------------------------------------------------
// <copyright file="CheckBackgroundJobApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Executor-level approval behavior for <see cref="CheckBackgroundJobTool"/>,
/// driven through <see cref="DispatchingToolExecutor"/> with a real
/// <see cref="ToolApprovalActor"/>.
/// </summary>
public sealed class CheckBackgroundJobApprovalTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    private static ToolConfig CreateConfig()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        return config;
    }

    [Fact]
    public async Task Status_and_cancel_from_non_interactive_turn_do_not_require_approval()
    {
        // The launch approval authorizes the job lifecycle. A non-interactive
        // follow-up must not request another approval that no user can answer.
        var config = CreateConfig();
        var fakeJobManager = Sys.ActorOf(Props.Create(() => new FakeJobStatusManager()), "fake-job-manager");

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), new ToolPathPolicy([]), new ShellCommandPolicy());
        registry.WithBackgroundJobTools(fakeJobManager);

        var approvalActor = Sys.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
        var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            approvalService);

        var nonInteractiveContext = TestToolExecutionContext.CreateBound("reminder/exec-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "slack",
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(false)
        });

        // Status query: must NOT throw ToolApprovalRequiredException.
        var statusCall = new FunctionCallContent(
            "call-status",
            "check_background_job",
            ToolInput.Create("JobId", "abc123"));
        _ = await executor.ExecuteAsync(statusCall, nonInteractiveContext, TestContext.Current.CancellationToken);

        // Cancellation only stops the session-owned process. It must not prompt.
        var cancelCall = new FunctionCallContent(
            "call-cancel",
            "check_background_job",
            ToolInput.Create("JobId", "abc123", "Cancel", true));
        _ = await executor.ExecuteAsync(cancelCall, nonInteractiveContext, TestContext.Current.CancellationToken);
    }

    private sealed class FakeJobStatusManager : ReceiveActor
    {
        public FakeJobStatusManager()
        {
            Receive<QueryBackgroundJob>(_ => Sender.Tell(new BackgroundJobStatusResponse
            {
                JobId = new BackgroundJobId("abc123"),
                Status = BackgroundJobStatus.Running,
                Found = false
            }));
            Receive<CancelBackgroundJob>(command =>
                Sender.Tell(new BackgroundJobCancelResponse(command.JobId, Found: true)));
        }
    }

    private sealed class StubRequiredActor : IRequiredActor<ToolApprovalActorKey>
    {
        private readonly IActorRef _actor;

        public StubRequiredActor(IActorRef actor) => _actor = actor;

        public IActorRef ActorRef => _actor;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_actor);
    }
}
