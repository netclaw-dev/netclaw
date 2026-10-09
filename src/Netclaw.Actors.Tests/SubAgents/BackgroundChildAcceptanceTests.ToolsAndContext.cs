// -----------------------------------------------------------------------
// <copyright file="BackgroundChildAcceptanceTests.ToolsAndContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed partial class BackgroundChildAcceptanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Run_context_is_bounded_and_confined_to_the_current_authority(bool publicAudience, bool foreignRequester)
    {
        var calls = Enumerable.Range(0, 10).Select(index => "call-" + index).ToArray();
        var state = AdmitBatch(AdmittedTask("original", SessionState.Empty), calls);
        foreach (var call in calls)
            state = state.Apply(new ChildRunAccepted { SessionId = Session, Run = Run(state, "run-" + call, call) });
        state = AdmittedTask("later", state);
        if (foreignRequester)
            state = state with { AdoptedTaskContext = state.AdoptedTaskContext! with { RequesterSenderId = new SenderId("other") } };
        var sessionDirectory = Path.Combine(Path.GetTempPath(), "neutral-child-context");
        var input = new ContextAssemblyInput(state, [], false, null, null, null, Session,
            SessionStoragePaths.CreateLegacy(sessionDirectory, Path.Combine(sessionDirectory, "session"), Session.Value), false, null, string.Empty,
            publicAudience ? TrustAudience.Public : TrustAudience.Personal);
        var context = SessionMessageAssembler.BuildVolatileContextBlock(input);
        if (publicAudience || foreignRequester)
        {
            Assert.DoesNotContain("[background-agent-runs]", context, StringComparison.Ordinal);
            Assert.DoesNotContain("run-call-", context, StringComparison.Ordinal);
            return;
        }
        Assert.Contains("[background-agent-runs]", context, StringComparison.Ordinal);
        Assert.Contains("load_tool(name: \"check_agent_run\")", context, StringComparison.Ordinal);
        Assert.Contains("Results arrive automatically.", context, StringComparison.Ordinal);
        using var runs = JsonDocument.Parse(context.Split("[background-agent-runs]\n", StringSplitOptions.None)[1].Split('\n')[0]);
        Assert.Equal(8, runs.RootElement.GetArrayLength());
        Assert.All(runs.RootElement.EnumerateArray(), run =>
        {
            Assert.Equal("Accepted", run.GetProperty("state").GetString());
            Assert.False(run.GetProperty("dispatch_closed").GetBoolean());
        });
        Assert.DoesNotContain("/project", context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_deferred_control_tool_loads_by_name_and_reports_the_real_owner_reply()
    {
        var registry = new ToolRegistry();
        var tool = new CheckAgentRunTool();
        registry.Register(tool);
        var policy = TestToolAccessPolicy.Create(new ToolConfig());
        Assert.False(registry.IsCoreTool(CheckAgentRunTool.ToolName));
        var state = AdmitBatch(AdmittedTask("original", SessionState.Empty), "start");
        var run = Run(state, "run-control", "start");
        var requests = new List<ChildControlRequest>();
        var context = TestToolExecutionContext.CreateBound(Session.Value, Path.Combine(Path.GetTempPath(), "neutral-child-control"), new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            SpawnChildActor = (payload, _, _) =>
            {
                requests.Add(Assert.IsType<ChildControlRequest>(payload));
                return Task.FromResult<object>(new ChildControlReply(run));
            }
        });
        var loaded = await new LoadToolTool(registry, policy).ExecuteAsync(
            new Dictionary<string, object?> { ["name"] = CheckAgentRunTool.ToolName }, context,
            TestContext.Current.CancellationToken);
        Assert.Equal(CheckAgentRunTool.ToolName, loaded);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["runId"] = run.RunId.Value, ["cancel"] = true },
            context, TestContext.Current.CancellationToken);
        Assert.Equal(new ChildControlRequest(run.RunId, true), Assert.Single(requests));
        Assert.IsType<ToolInvocationReceipt.Succeeded>(context.Outputs.Receipt);
        using var body = JsonDocument.Parse(result);
        Assert.Equal("Accepted", body.RootElement.GetProperty("state").GetString());
        Assert.False(body.RootElement.GetProperty("cancellation_requested").GetBoolean());
        Assert.False(body.RootElement.GetProperty("dispatch_closed").GetBoolean());
        Assert.False(SubAgentToolPolicy.IsAllowedForSubAgent(CheckAgentRunTool.ToolName));
    }
}
