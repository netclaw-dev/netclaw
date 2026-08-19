// -----------------------------------------------------------------------
// <copyright file="SubAgentObservabilityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Observability coverage for <see cref="SubAgentActor"/>: progress phases and
/// lifecycle events must surface in the logs (and therefore Seq) even on the
/// non-streaming spawn path, where the actor has no activity sink to write to.
/// Before the observability work these phases only reached a parent-owned channel
/// (or, with no sink, nothing at all). Regression coverage for issues #1429/#1431.
/// </summary>
public sealed class SubAgentObservabilityTests : TestKit
{
    public SubAgentObservabilityTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // INFO so the EventFilter assertions on the sub-agent's progress and
        // lifecycle logs are deterministic.
        builder.AddHocon("akka.loglevel = INFO", HoconAddMode.Prepend);
    }

    private static SubAgentDefinition CreateDefinition(IReadOnlyList<INetclawTool>? tools = null)
        => new()
        {
            Name = new AgentName("test-agent"),
            SystemPrompt = "You are a test agent.",
            Tools = tools ?? [],
            EmitStructuredFindings = false
        };

    private static ToolAccessPolicy PermissivePolicy() => new(
        new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
        new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal, ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
        new ShellCommandPolicy(),
        new ToolPathPolicy([]));

    private static RunSubAgent NewRun(string task, string? scopeId = null)
        => new()
        {
            Scope = SubAgentTestScope.Create(
                scopeId: scopeId ?? "test-session/subagent/test/run"),
            Task = task,
            Timeout = TimeSpan.FromSeconds(5)
        };

    [Fact]
    public async Task Progress_phase_is_logged_on_the_non_streaming_path()
    {
        // No ActivitySink is supplied (the non-streaming spawn_agent path). Before
        // the fix EmitActivity was a no-op here, so a long run emitted almost nothing
        // between start and completion. Now the phase is logged, so it is visible and
        // diagnosable in Seq. The scope id mirrors a real spawn so the SessionId/
        // SubSessionId enrichment branch is exercised too.
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), new FakeChatClient(), PermissivePolicy()));

        await EventFilter.Info(contains: "calling the model").ExpectAsync(1, async () =>
        {
            await agent.Ask<SubAgentResult>(
                NewRun("Say hello", "console/C123/subagent/test-agent/run-abc"),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Completion_emits_summary_with_cumulative_stats()
    {
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), new FakeChatClient(), PermissivePolicy()));

        // The summary line carries the cumulative tool/iteration/duration stats used
        // for sub-agent run analysis.
        await EventFilter.Info(contains: "summary:").ExpectAsync(1, async () =>
        {
            await agent.Ask<SubAgentResult>(
                NewRun("Say hello"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Startup_emits_one_payload_free_tool_exposure_diagnostic()
    {
        var core = new FakeNetclawTool("core_marker", "core result");
        var deferred = new FakeNetclawTool("deferred_marker", "deferred result");
        var props = SubAgentActor.CreatePropsWithProjectInstructionProvider(
            CreateDefinition([core, deferred]),
            new FakeChatClient(),
            PermissivePolicy(),
            NullSystemPromptProvider.Instance,
            coreToolNames: new HashSet<string>([core.Name], StringComparer.Ordinal));
        var agent = Sys.ActorOf(props);

        await EventFilter.Info(
            message: "SubAgent tool exposure core=1 deferredVisible=1 loaded=0")
            .ExpectAsync(1, async () =>
            {
                await agent.Ask<SubAgentResult>(
                    NewRun("Inspect /private/payload-marker.txt"),
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tool_dispatch_logs_a_tool_start_event_with_call_id()
    {
        var fakeTool = new FakeNetclawTool("greet", "Hello from tool!");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-1", "greet",
                    new Dictionary<string, object?> { ["name"] = "World" })
            ]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([fakeTool]), fakeClient, PermissivePolicy()));

        // A tool start event (distinct from the existing tool-result log) lets an
        // operator see when a slow tool began, not just when it finished.
        await EventFilter.Info(contains: "tool start callId=call-1 name=greet").ExpectAsync(1, async () =>
        {
            await agent.Ask<SubAgentResult>(
                NewRun("Greet the user"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    // Regression coverage for issue #1597: sub-agent LLM calls used to discard
    // ChatResponse.Usage entirely — the actor had no ISessionMetrics and never read
    // response.Usage — so every sub-agent's token consumption was invisible to
    // `netclaw stats`. These tests pin the sub-agent to the shared daily-stats sink.

    [Fact]
    public async Task Records_token_usage_to_session_metrics_on_text_response()
    {
        var metrics = new RecordingSessionMetrics();
        var fakeClient = new FakeChatClient
        {
            UsageOverride = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 45 }
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition(), fakeClient, PermissivePolicy(), sessionMetrics: metrics));

        var result = await agent.Ask<SubAgentResult>(
            NewRun("Say hello"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // A single LLM call → exactly one usage record billed to the shared
        // process-wide daily-stats sink (the same singleton the parent session uses).
        var call = Assert.Single(metrics.TokenUsageCalls);
        Assert.Equal((120L, 45L), call);
    }

    [Fact]
    public async Task Records_token_usage_for_every_llm_call_across_the_turn_loop()
    {
        // A tool-call turn followed by a final-text turn = two LLM calls. Both must be
        // billed. This is the crux of #1597: the sub-agent's INTERNAL calls (not just
        // its single final output) have to reach `netclaw stats`, so the recorded total
        // is the per-call usage summed — not one call's worth.
        var metrics = new RecordingSessionMetrics();
        var fakeTool = new FakeNetclawTool("greet", "Hello from tool!");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-1", "greet",
                    new Dictionary<string, object?> { ["name"] = "World" })
            ],
            UsageOverride = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 45 }
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([fakeTool]), fakeClient, PermissivePolicy(), sessionMetrics: metrics));

        var result = await agent.Ask<SubAgentResult>(
            NewRun("Greet the user"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(2, fakeClient.CallCount);
        Assert.Equal(2, metrics.TokenUsageCalls.Count);
        Assert.Equal(240L, metrics.TotalInputTokens);
        Assert.Equal(90L, metrics.TotalOutputTokens);
    }

    [Fact]
    public async Task Completion_summary_reports_cumulative_token_totals()
    {
        var fakeClient = new FakeChatClient
        {
            UsageOverride = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 45 }
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), fakeClient, PermissivePolicy()));

        // The completion summary now carries token totals so sub-agent cost is visible
        // in the logs (and Seq), not just tool/iteration/duration counts.
        await EventFilter.Info(contains: "inputTokens=120, outputTokens=45").ExpectAsync(1, async () =>
        {
            await agent.Ask<SubAgentResult>(
                NewRun("Say hello"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }
}
