// -----------------------------------------------------------------------
// <copyright file="MessyCommandOneTimeApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using Xunit.v3;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Verifies the messy-command branch of <c>IsOneTimeApprovalSatisfied</c>:
/// bash control-flow constructs (for/while/case) and unbalanced
/// quotes/brackets cannot have verb-chain patterns extracted, so the
/// approval prompt offers Once + Deny only and
/// <c>ApprovalContext.Patterns</c> is empty. The OneTimeApproval bypass
/// must succeed on tool-name match alone for messy commands — otherwise
/// clicking Once on a complex bash for-loop fails the retry with
/// <c>ToolApprovalRequiredException</c>, surfacing as
/// "I encountered an error executing a tool" with a correlation ID.
/// </summary>
/// <remarks>
/// Repro on production: session
/// <c>D0AC6CKBK5K/1778542266.328629</c> on 2026-05-11 ran a
/// <c>for repo in ...; do ... worktree list ...; done</c> loop. Prompt
/// fired with "complex command — only Once available." User clicked
/// Once 28 minutes later (no longer auto-denied since the
/// approval-timeout removal). Click landed on the now-living workflow,
/// retry hit the bypass guard, threw.
/// </remarks>
public sealed class MessyCommandOneTimeApprovalTests : TestKit
{
    public MessyCommandOneTimeApprovalTests(ITestOutputHelper output) : base(output: output)
    {
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        services.AddSingleton(toolConfig);
        services.AddSingleton(new EffectivePolicyDefaults(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false));
        services.AddSingleton<ToolAccessPolicy>(sp => new ToolAccessPolicy(
            sp.GetRequiredService<ToolConfig>(),
            sp.GetRequiredService<EffectivePolicyDefaults>(),
            new Netclaw.Security.ShellCommandPolicy(),
            new Netclaw.Security.ToolPathPolicy([])));

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(toolConfig, new NetclawPaths(), new Netclaw.Security.ToolPathPolicy([]), new Netclaw.Security.ShellCommandPolicy());
        services.AddSingleton(registry);
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithActors((system, actorRegistry, resolver) =>
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            actorRegistry.Register<ToolApprovalActor>(approvalActor);
        });
    }

    [Fact]
    public async Task ApprovedOnce_on_messy_command_satisfies_one_time_bypass()
    {
        var registry = Host.Services.GetRequiredService<ToolRegistry>();
        var policy = Host.Services.GetRequiredService<ToolAccessPolicy>();
        var approvalActor = ActorRegistry.Get<ToolApprovalActor>();
        var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
        var executor = new DispatchingToolExecutor(registry, policy, approvalService);

        // The runtime iterator is unresolved, so the command remains messy
        // even though bounded literal loops can now publish authored facts.
        var toolCall = new FunctionCallContent(
            "call-messy-once",
            "shell_execute",
            ToolInput.Create(
                "Command",
                "for i in $(printf '1 2 3'); do echo \"$i\"; done",
                "_rationale",
                "Verify one-time approval for a complex command."));

        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr",
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
        });

        var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
            executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

        // Confirm the test setup: a messy command produces empty patterns
        // and the IsMessy flag.
        Assert.True(firstAttempt.ApprovalContext.IsMessy);
        Assert.Empty(firstAttempt.ApprovalContext.Patterns);

        // Simulate ApprovedOnce: SessionToolExecutionPipeline sets the
        // tool-name and patterns on the context. For messy commands the
        // patterns list is empty (per ApprovalContext.Patterns above), so
        // the bypass must rely on tool-name match only.
        context.OneTimeApprovedToolName = toolCall.Name;
        context.SetOneTimeApprovedPatterns(OneTimeApprovalKeys.Create(firstAttempt.ApprovalContext));

        // The retry must succeed without throwing. Output text varies by
        // environment (bash for-loop expansion); the load-bearing assertion
        // is the absence of ToolApprovalRequiredException.
        _ = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

        // After the per-retry cleanup runs, the bypass is gone and a
        // subsequent attempt re-prompts.
        context.OneTimeApprovedToolName = null;
        context.SetOneTimeApprovedPatterns([]);

        await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
            executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
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
