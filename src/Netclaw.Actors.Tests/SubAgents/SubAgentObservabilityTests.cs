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
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

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

    private static RunSubAgent NewRun(string task, string? scopeId = null)
        => new()
        {
            Task = task,
            Timeout = TimeSpan.FromSeconds(5),
            Audience = TrustAudience.Personal,
            SessionScopeId = scopeId
        };

    [Fact]
    public async Task Progress_phase_is_logged_on_the_non_streaming_path()
    {
        // No ActivitySink is supplied (the non-streaming spawn_agent path). Before
        // the fix EmitActivity was a no-op here, so a long run emitted almost nothing
        // between start and completion. Now the phase is logged, so it is visible and
        // diagnosable in Seq. The scope id mirrors a real spawn so the SessionId/
        // SubSessionId enrichment branch is exercised too.
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), new FakeChatClient()));

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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), new FakeChatClient()));

        // The summary line carries the cumulative tool/iteration/duration stats used
        // for sub-agent run analysis.
        await EventFilter.Info(contains: "summary:").ExpectAsync(1, async () =>
        {
            await agent.Ask<SubAgentResult>(
                NewRun("Say hello"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([fakeTool]), fakeClient));

        // A tool start event (distinct from the existing tool-result log) lets an
        // operator see when a slow tool began, not just when it finished.
        await EventFilter.Info(contains: "tool start callId=call-1 name=greet").ExpectAsync(1, async () =>
        {
            await agent.Ask<SubAgentResult>(
                NewRun("Greet the user"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
    }
}
