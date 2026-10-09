// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.StartAdapters.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security.Skills;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using ModelChatRole = Microsoft.Extensions.AI.ChatRole;
using SkillScanResult = Netclaw.Security.Skills.SkillScanResult;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Theory]
    [InlineData("scope")]
    [InlineData("digest")]
    [InlineData("agent")]
    [InlineData("owner")]
    [InlineData("state")]
    public async Task Spawn_agent_rejects_malformed_typed_owner_acceptance_before_a_success_receipt(string defect)
    {
        var tool = ConfigureCanonicalSpawnAdapter();
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await SendOriginalAsync(manager);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var admission = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
        var original = Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(Retry(admission),
            Ceiling, TestContext.Current.CancellationToken));
        Assert.Equal(BackgroundChildState.Running, original.State);
        var run = original.Run;
        var state = original.State;
        switch (defect)
        {
            case "scope": run = run with { ScopeId = new SubAgentScopeId("signalr/foreign/subagent/worker/other") }; break;
            case "digest": run = run with { ArgumentsDigest = new string('F', 64) }; break;
            case "agent": run = run with { AgentName = new AgentName("other-worker"),
                ScopeId = new SubAgentScopeId($"{Session.Value}/subagent/other-worker/{run.RunId.Value}") }; break;
            case "owner":
                var foreign = new SessionId("signalr/foreign-owner");
                run = run with { StartKey = run.StartKey with { SessionId = foreign },
                    OriginalContext = run.OriginalContext with { SessionId = foreign },
                    ScopeId = new SubAgentScopeId($"{foreign.Value}/subagent/worker/{run.RunId.Value}") };
                break;
            case "state": state = BackgroundChildState.Completed; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }
        if (defect != "scope") run.Validate();
        var outputs = new ToolExecutionOutputs();
        var calls = 0;
        var scope = _start!.StartInvocation!.RunScope with { SpawnChildActor = (payload, _, _) =>
        {
            var prepared = Assert.IsType<PreparedChildRun>(payload);
            Assert.Equal(admission.Run.ArgumentsDigest, prepared.ArgumentsDigest);
            Interlocked.Increment(ref calls);
            return Task.FromResult<object>(new ChildStartReply.Accepted(run, state));
        }};
        await Assert.ThrowsAsync<InvalidDataException>(() => tool.ExecuteAsync(SpawnArguments(),
            new ToolInvocationContext(scope, _start.StartInvocation.ExecutionTimeout, outputs), TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Null(outputs.Receipt);
        Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
        Assert.Equal(1, _child.CallCount);
        Assert.False(_childRelease.Task.IsCompleted);
    }

    [Fact]
    public async Task Spawn_agent_accepts_a_valid_running_retry_with_the_original_run_instead_of_the_new_proposal()
    {
        var tool = ConfigureCanonicalSpawnAdapter();
        var (owner, manager, subscriber) = await CreateOwnerAsync();
        await SendOriginalAsync(manager);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var admission = Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
        var retry = Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(Retry(admission),
            Ceiling, TestContext.Current.CancellationToken));
        Assert.Equal(BackgroundChildState.Running, retry.State);
        var outputs = new ToolExecutionOutputs();
        var scope = _start!.StartInvocation!.RunScope with { SpawnChildActor = (payload, _, _) =>
        {
            var proposed = Assert.IsType<PreparedChildRun>(payload);
            Assert.NotEqual(retry.Run.RunId, proposed.RunId);
            Assert.Equal(retry.Run.ArgumentsDigest, proposed.ArgumentsDigest);
            return Task.FromResult<object>(retry);
        }};
        var result = await tool.ExecuteAsync(SpawnArguments(),
            new ToolInvocationContext(scope, _start.StartInvocation.ExecutionTimeout, outputs), TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(result);
        Assert.Equal(retry.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
        Assert.Equal(retry.Run.ScopeId.Value, body.RootElement.GetProperty("scope_id").GetString());
        Assert.Equal("Running", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("check_agent_run", body.RootElement.GetProperty("control_tool").GetString());
        Assert.IsType<ToolInvocationReceipt.Succeeded>(outputs.Receipt);
        Assert.Single((await ReadJournalAsync()).OfType<ChildRunAccepted>());
        Assert.Equal(1, _child.CallCount);
        Assert.False(_childRelease.Task.IsCompleted);
    }

    [Fact]
    public async Task Spawn_agent_cannot_return_acceptance_from_a_failed_owner_commit()
    {
        ConfigureCanonicalSpawnAdapter();
        var entered = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not ChildRunAccepted) return false;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return true;
        });
        var (owner, manager, _) = await CreateOwnerAsync();
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            await EventFilter.Error(contains: "Failed to persist event type").ExpectOneAsync(async () =>
            {
                await SendOriginalAsync(manager);
                await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
                Assert.Equal(0, _child.CallCount);
                Assert.Null(_start!.StartInvocation!.Outputs.Receipt);
                release.TrySetResult();
                await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);
            var events = await ReadJournalAsync();
            Assert.Single(events.OfType<ToolBatchStarted>());
            Assert.Empty(events.OfType<ChildRunAccepted>());
            Assert.Empty(events.OfType<ChildRunEvent.Started>());
            Assert.Empty(events.OfType<ToolCallRecorded>());
            Assert.Null(_start!.StartInvocation!.Outputs.Receipt);
            Assert.Equal(0, _child.CallCount);
            Assert.Equal(1, _main.CallCount);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Routed_skill_load_scans_before_durable_acceptance_and_preserves_the_additive_overlay(bool reject)
    {
        const string content = "---\nname: route-probe\ndescription: Neutral route.\nmetadata:\n  subagent: worker\n---\n\nInspect the neutral route marker.\n";
        var file = Path.Combine(_directory!.Paths.SkillsDirectory, "route-probe", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, content, TestContext.Current.CancellationToken);
        var skills = new SkillRegistry();
        var scanned = SkillScanner.Scan(_directory.Paths.SkillsDirectory);
        Assert.Empty(scanned.Issues);
        skills.ReplaceAll(scanned.AcceptedSkills);
        var scanner = new AdapterContentScanner(reject);
        _start!.StartTool = new SkillLoadTool(skills, scanner, new AdapterPromptLoader(),
            subAgentRegistry: AdapterProfiles(), subAgentSpawner: _start.Spawner);
        _main.ToolCallsOnFirstCall = [new FunctionCallContent("start-1", "skill_load", new Dictionary<string, object?>
        { ["Name"] = "route-probe", ["Task"] = "Inspect the neutral fixture.", ["Context"] = "route-context-marker",
            ["_rationale"] = "Use the neutral routed skill." })];
        var (_, manager, subscriber) = await CreateOwnerAsync();
        await SendOriginalAsync(manager);
        var output = Assert.IsType<ToolResultOutput>(await subscriber.FishForMessageAsync<object>(
            message => message is ToolResultOutput, Ceiling, cancellationToken: TestContext.Current.CancellationToken));
        await CompletedAsync(subscriber);
        var scan = Assert.Single(scanner.Calls);
        Assert.Equal("route-probe", scan.Name);
        Assert.Equal(content, scan.Content);
        var events = await ReadJournalAsync();
        var recorded = Assert.Single(events.OfType<ToolCallRecorded>());
        Assert.Equal(output.Result, recorded.ToolResult.Content);
        var paired = Assert.Single(_main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()));
        Assert.Equal("start-1", paired.CallId);
        Assert.Equal(output.Result, Assert.IsType<string>(paired.Result));
        if (reject)
        {
            Assert.Equal("Skill 'route-probe' blocked by content scan: neutral rejection", output.Result);
            Assert.Empty(events.OfType<ChildRunAccepted>());
            Assert.Empty(events.OfType<ChildRunEvent.Started>());
            Assert.Equal(0, _child.CallCount);
            return;
        }
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        var admission = Assert.Single(events.OfType<ChildRunAccepted>());
        admission.Run.Validate();
        Assert.Equal("skill_load", admission.Run.SourceOperation);
        Assert.Equal(new ToolCallId("start-1"), Assert.IsType<ChildRunStartKey.Tool>(admission.Run.StartKey).CallId);
        using var body = JsonDocument.Parse(output.Result);
        Assert.Equal(4, body.RootElement.EnumerateObject().Count());
        Assert.Equal(admission.Run.RunId.Value, body.RootElement.GetProperty("run_id").GetString());
        Assert.Equal(admission.Run.ScopeId.Value, body.RootElement.GetProperty("scope_id").GetString());
        Assert.Equal("Accepted", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("check_agent_run", body.RootElement.GetProperty("control_tool").GetString());
        var system = string.Join("\n", _child.ReceivedMessages[0].Where(message => message.Role == ModelChatRole.System).Select(message => message.Text));
        Assert.Contains("Complete the assigned task.", system, StringComparison.Ordinal);
        Assert.Contains("[Skill Overlay]\nInspect the neutral route marker.", system, StringComparison.Ordinal);
        Assert.Contains(_child.ReceivedMessages[0], message => message.Role == ModelChatRole.User
            && message.Text.Contains("route-context-marker", StringComparison.Ordinal));
        Assert.Empty(events.OfType<ChildRunEvent.TerminalRecorded>());
        Assert.Equal(2, _main.CallCount);
        Assert.Equal(1, _child.CallCount);
        Assert.False(_childRelease.Task.IsCompleted);
    }

    private SpawnAgentTool ConfigureCanonicalSpawnAdapter()
    {
        var tool = new SpawnAgentTool(AdapterProfiles(), _start!.Spawner, _directory!.Paths);
        _start.StartTool = tool;
        _main.ToolCallsOnFirstCall = [new FunctionCallContent("start-1", "spawn_agent", SpawnArguments())];
        return tool;
    }

    private static Dictionary<string, object?> SpawnArguments() => new()
    { ["Agent"] = "worker", ["Task"] = "Inspect the neutral fixture.", ["_rationale"] = "Delegate the neutral task." };

    private static SubAgentDefinitionRegistry AdapterProfiles()
    {
        var profiles = new SubAgentDefinitionRegistry();
        profiles.Register(new SubAgentProfile
        {
            Name = "worker", Description = "Complete the neutral task.", SystemPrompt = "Complete the assigned task.",
            ModelRole = ModelRole.Compaction, ToolNames = ["file_read"], EmitStructuredFindings = false,
            Visibility = SubAgentVisibility.UserFacing
        });
        return profiles;
    }

    private sealed class AdapterContentScanner(bool reject) : ISkillContentScanner
    {
        public List<(string Name, string Content)> Calls { get; } = [];
        public Task<SkillScanResult> ScanAsync(string skillName, string content, CancellationToken cancellationToken = default)
        {
            Calls.Add((skillName, content));
            return Task.FromResult(reject ? SkillScanResult.Reject("neutral rejection") : SkillScanResult.Allow());
        }
    }

    private sealed class AdapterPromptLoader : IMcpPromptSkillLoader
    {
        public ValueTask<McpPromptSkillLoadResult> LoadAsync(McpPromptSkillSource source,
            IReadOnlyDictionary<string, string>? arguments, ToolInvocationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The file route must not request an MCP prompt.");
    }
}
