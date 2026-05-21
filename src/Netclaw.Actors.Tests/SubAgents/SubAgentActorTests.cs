// -----------------------------------------------------------------------
// <copyright file="SubAgentActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Memory;
using ApprovalOptionKeys = Netclaw.Actors.Protocol.ApprovalOptionKeys;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

public class SubAgentActorTests : TestKit
{
    public SubAgentActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No persistence or hosting needed — SubAgentActor is standalone
    }

    private static SubAgentDefinition CreateDefinition(IReadOnlyList<INetclawTool>? tools = null)
    {
        return new SubAgentDefinition
        {
            Name = new AgentName("test-agent"),
            SystemPrompt = "You are a test agent.",
            Tools = tools ?? [],
            EmitStructuredFindings = false
        };
    }

    [Fact]
    public async Task Text_response_returns_success_result()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Say hello", Timeout = TimeSpan.FromSeconds(5) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("Response #1", result.Output);
        Assert.Equal("test-agent", result.AgentName.Value);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Spawn_without_audience_fails_fast_with_unsuccessful_result()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        // A RunSubAgent with no audience must not run — the sub-agent must reply
        // with an unsuccessful result immediately, not crash and make the caller
        // wait out the Ask timeout. A generous Ask timeout would still elapse if
        // the actor merely threw; this asserts the prompt failure reply.
        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Do the thing", Timeout = TimeSpan.FromSeconds(5), Audience = null },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("audience", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tool_call_executes_and_continues()
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

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Greet the user", Timeout = TimeSpan.FromSeconds(5) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(fakeTool.WasCalled);
        Assert.NotNull(fakeTool.LastContext);
        // Second LLM call returns text (tool calls only on first call)
        Assert.Contains("Response #2", result.Output);
    }

    [Fact]
    public async Task Tool_execution_inherits_parent_session_and_project_directories()
    {
        var fakeTool = new FakeNetclawTool("inspect_context", "ok");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-context", "inspect_context")
            ]
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Inspect the inherited paths.",
                Timeout = TimeSpan.FromSeconds(5),
                ParentSessionDirectory = "/tmp/netclaw/sessions/abc",
                ParentProjectDirectory = "/home/user/workspaces/netclaw",
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.Equal("/tmp/netclaw/sessions/abc", fakeTool.LastContext!.SessionDirectory);
        Assert.Equal("/home/user/workspaces/netclaw", fakeTool.LastContext.ProjectDirectory);
    }

    [Fact]
    public async Task Tool_execution_with_no_parent_project_directory_passes_null_through()
    {
        var fakeTool = new FakeNetclawTool("inspect_context", "ok");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [new FunctionCallContent("call-no-project", "inspect_context")]
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Inspect inherited paths.",
                Timeout = TimeSpan.FromSeconds(5),
                ParentSessionDirectory = "/tmp/netclaw/sessions/xyz",
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.Equal("/tmp/netclaw/sessions/xyz", fakeTool.LastContext!.SessionDirectory);
        Assert.Null(fakeTool.LastContext.ProjectDirectory);
    }

    [Fact]
    public async Task Tool_execution_inherits_parent_resolved_cwd_snapshot()
    {
        var fakeTool = new FakeNetclawTool("inspect_context", "ok");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [new FunctionCallContent("call-cwd", "inspect_context")]
        };

        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([fakeTool]), fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Inspect inherited cwd.",
                Timeout = TimeSpan.FromSeconds(5),
                ParentSessionDirectory = "/tmp/netclaw/sessions/parent",
                ParentProjectDirectory = "/home/user/repos/foo",
                ParentCwd = "/home/user/repos/foo",
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.Equal("/home/user/repos/foo", fakeTool.LastContext!.InheritedCwd);
        // ProjectDirectory wins the resolve when set; this asserts that the
        // inherited snapshot doesn't shadow it.
        Assert.Equal("/home/user/repos/foo", fakeTool.LastContext.ResolveShellCwd(null));
    }

    [Fact]
    public async Task Tool_execution_with_null_parent_cwd_resolves_to_session_dir_or_null()
    {
        var fakeTool = new FakeNetclawTool("inspect_context", "ok");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [new FunctionCallContent("call-null-cwd", "inspect_context")]
        };

        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([fakeTool]), fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Inspect null cwd.",
                Timeout = TimeSpan.FromSeconds(5),
                ParentCwd = null,
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.Null(fakeTool.LastContext!.InheritedCwd);
        Assert.Null(fakeTool.LastContext.ResolveShellCwd(null));
    }

    [Fact]
    public async Task Tool_execution_inherits_parent_cwd_when_child_has_no_project_or_session_dir()
    {
        // The original bug shape: a sub-agent whose parent had a resolved cwd
        // but no ProjectDirectory/SessionDirectory propagating to the child.
        // InheritedCwd is the only path that surfaces the parent's effective
        // working directory to the approval gate; without it, the prompt
        // header reads "(no working directory)".
        var fakeTool = new FakeNetclawTool("inspect_context", "ok");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [new FunctionCallContent("call-inherit-only", "inspect_context")]
        };

        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([fakeTool]), fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Inspect inherited cwd with no other sources.",
                Timeout = TimeSpan.FromSeconds(5),
                ParentSessionDirectory = null,
                ParentProjectDirectory = null,
                ParentCwd = "/home/user/repos/foo",
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.Equal("/home/user/repos/foo", fakeTool.LastContext!.ResolveShellCwd(null));
    }

    [Fact]
    public async Task Each_spawn_snapshots_its_own_parent_project_directory()
    {
        // Mirrors D6: parent project changes between two activations show up
        // in the second subagent run but never leak into the first.
        var firstTool = new FakeNetclawTool("inspect_context", "ok");
        var firstClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [new FunctionCallContent("call-1", "inspect_context")]
        };
        var firstAgent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([firstTool]), firstClient));

        var firstResult = await firstAgent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "First run.",
                Timeout = TimeSpan.FromSeconds(5),
                ParentProjectDirectory = "/home/user/workspaces/project-a",
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(firstResult.Success);
        Assert.Equal("/home/user/workspaces/project-a", firstTool.LastContext!.ProjectDirectory);

        var secondTool = new FakeNetclawTool("inspect_context", "ok");
        var secondClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [new FunctionCallContent("call-2", "inspect_context")]
        };
        var secondAgent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([secondTool]), secondClient));

        var secondResult = await secondAgent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Second run after parent project switch.",
                Timeout = TimeSpan.FromSeconds(5),
                ParentProjectDirectory = "/home/user/workspaces/project-b",
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(secondResult.Success);
        Assert.Equal("/home/user/workspaces/project-b", secondTool.LastContext!.ProjectDirectory);
        Assert.Equal("/home/user/workspaces/project-a", firstTool.LastContext!.ProjectDirectory);
    }

    [Fact]
    public async Task System_prompt_includes_inherited_project_instructions_when_present()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition() with { ProjectInstructions = "Project rules: prefer C#." };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Do the thing.", Timeout = TimeSpan.FromSeconds(5) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        var systemMessage = fakeClient.LastReceivedMessages!.Single(m => m.Role == ChatRole.System);
        Assert.Contains("You are a test agent.", systemMessage.Text);
        Assert.Contains("Project rules: prefer C#.", systemMessage.Text);
    }

    [Fact]
    public async Task System_prompt_omits_project_section_when_no_instructions_inherited()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Do the thing.", Timeout = TimeSpan.FromSeconds(5) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        var systemMessage = fakeClient.LastReceivedMessages!.Single(m => m.Role == ChatRole.System);
        Assert.Equal("You are a test agent.", systemMessage.Text);
    }

    [Fact]
    public async Task Approval_gated_tool_is_denied_inside_subagent()
    {
        var fakeTool = new FakeNetclawTool("shell_execute", "should not run");
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var policy = new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-approval", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy, approvalService: null));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Try the shell tool", Timeout = TimeSpan.FromSeconds(5) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(fakeTool.WasCalled);
        Assert.Contains("Response #2", result.Output);
    }

    [Fact]
    public async Task Subagent_approval_request_carries_cwd_candidates_and_full_button_set()
    {
        // Regression: the parent approval bridge previously hardcoded a
        // 4-button list and dropped Cwd/Candidates entirely, so sub-agent
        // approval prompts showed "(no working directory)" and were missing
        // the Always-anywhere button regardless of what the sub-agent's
        // resolved cwd was.
        var fakeTool = new FakeNetclawTool("shell_execute", "ok");
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var policy = new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());

        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-cwd-prompt", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        var approvalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce);
        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy, approvalService: null));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(5),
                Audience = TrustAudience.Personal,
                ParentSessionDirectory = "/tmp/netclaw/sessions/parent",
                ParentProjectDirectory = "/home/user/repos/foo",
                ParentCwd = "/home/user/repos/foo",
                ApprovalBridge = approvalBridge,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, approvalBridge.RequestCount);
        Assert.Equal("/home/user/repos/foo", approvalBridge.RequestedCwd);
        Assert.Single(approvalBridge.RequestedCandidates);
        Assert.Equal("git push origin main", approvalBridge.RequestedCandidates[0].Verb);
        Assert.Contains(approvalBridge.RequestedOptions, o => o.Key == ApprovalOptionKeys.ApproveEverywhere);
        Assert.Contains(approvalBridge.RequestedOptions, o => o.Key == ApprovalOptionKeys.ApproveAlways);
        Assert.Contains(approvalBridge.RequestedOptions, o => o.Key == ApprovalOptionKeys.ApproveSession);
    }

    [Fact]
    public async Task Approve_once_does_not_leak_between_subagent_tool_calls()
    {
        var fakeTool = new FakeNetclawTool("shell_execute", "ok");
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var policy = new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());
        var fakeClient = new SequencedToolCallChatClient(
            [
                new FunctionCallContent(
                    "call-approval-1",
                    "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" }),
                new FunctionCallContent(
                    "call-approval-2",
                    "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]);
        var approvalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce);

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy, approvalService: null));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Run the same approval-gated tool twice",
                Timeout = TimeSpan.FromSeconds(5),
                ApprovalBridge = approvalBridge,
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(2, approvalBridge.RequestCount);
        Assert.Equal(["git push origin main", "git push origin main"], approvalBridge.RequestedPatterns);
    }

    [Fact]
    public async Task Max_iterations_forces_text_response()
    {
        var fakeTool = new FakeNetclawTool("looper", "loop result");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-loop", "looper")
            ],
            AlwaysReturnToolCalls = true
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Loop forever", Timeout = TimeSpan.FromSeconds(10) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // After 10 tool iterations, forces a no-tools call which returns text
        Assert.True(result.Success);
        // Should have made multiple calls: 10 tool calls + 1 initial + 1 forced text
        Assert.True(fakeClient.CallCount >= 11);
    }

    [Fact]
    public async Task Timeout_returns_failure()
    {
        var fakeClient = new FakeChatClient
        {
            Delay = TimeSpan.FromSeconds(30) // Much longer than timeout
        };
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Slow task", Timeout = TimeSpan.FromMilliseconds(500) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task LLM_failure_returns_failure()
    {
        var throwingClient = new ThrowingChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, throwingClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Fail", Timeout = TimeSpan.FromSeconds(5) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("LLM call failed", result.Output);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Actor_stops_after_completion()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));
        Watch(agent);

        agent.Tell(new RunSubAgent { Task = "Done", Timeout = TimeSpan.FromSeconds(5) , Audience = TrustAudience.Personal });

        // SubAgentResult arrives before Terminated — drain it first
        await ExpectMsgAsync<SubAgentResult>(cancellationToken: TestContext.Current.CancellationToken);
        await ExpectTerminatedAsync(agent, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tool_execution_uses_session_scope_for_mcp_invocation()
    {
        var invoker = new RecordingMcpToolInvoker("ok");
        var fakePlaywrightTool = new McpToolAdapter(
            AIFunctionFactory.Create((string url) => url, "navigate_page"),
            "browser_playwright",
            "navigate_page",
            invoker: invoker);

        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent(
                    "call-1",
                    "browser_playwright/navigate_page",
                    new Dictionary<string, object?> { ["url"] = "https://example.com" })
            ]
        };

        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Team.McpServersMode = ToolProfileMode.All;
        var policy = new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());

        var definition = CreateDefinition([fakePlaywrightTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy, approvalService: null));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Open example.com",
                Timeout = TimeSpan.FromSeconds(5),
                SessionScopeId = "session/subagent-scope",
                Audience = TrustAudience.Team
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("session/subagent-scope", invoker.SessionId);
        Assert.Equal(TrustAudience.Team, invoker.Audience);
        Assert.Equal("browser_playwright", invoker.ServerName);
        Assert.Equal("navigate_page", invoker.ToolName);
    }

    [Fact]
    public async Task Long_text_response_does_not_emit_findings_by_default()
    {
        var fakeClient = new FakeChatClient
        {
            ResponseText = "This is a durable subagent summary with enough detail to be considered a memory candidate for parent-session checkpoint review."
        };
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Summarize research", Timeout = TimeSpan.FromSeconds(5) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Long_text_response_emits_findings_when_enabled()
    {
        var fakeClient = new FakeChatClient
        {
            ResponseText = "This is a durable subagent summary with enough detail to be considered a memory candidate for parent-session checkpoint review."
        };
        var definition = CreateDefinition() with { EmitStructuredFindings = true };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Summarize research", Timeout = TimeSpan.FromSeconds(5) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Single(result.Findings);
        Assert.Equal(SubAgentFindingShape.Conclusion, result.Findings[0].Shape);
        Assert.Equal("subagent:test-agent", result.Findings[0].Title);
        Assert.Equal(SubAgentFindingDurability.Durable, result.Findings[0].Durability);
        Assert.Equal(SubAgentFindingReusability.Reusable, result.Findings[0].Reusability);
        Assert.Equal(SubAgentFindingRecallMode.Searchable, result.Findings[0].RecallMode);
    }

    [Fact]
    public async Task RuntimeContext_is_prefixed_onto_first_user_message()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Summarize the recent commits.",
                RuntimeContext = "Workspace is netclaw on branch feature/foo.",
                Timeout = TimeSpan.FromSeconds(5),
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);

        // System prompt is message 0, user message is message 1.
        Assert.Equal(2, fakeClient.LastReceivedMessages.Count);
        Assert.Equal(ChatRole.User, fakeClient.LastReceivedMessages[1].Role);

        var userText = fakeClient.LastReceivedMessages[1].Text;
        Assert.Contains("Context:", userText);
        Assert.Contains("Workspace is netclaw on branch feature/foo.", userText);
        Assert.Contains("Task:", userText);
        Assert.Contains("Summarize the recent commits.", userText);
    }

    [Fact]
    public async Task Null_RuntimeContext_leaves_first_user_message_as_raw_task()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Do the thing.",
                Timeout = TimeSpan.FromSeconds(5),
                Audience = TrustAudience.Personal,
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        Assert.Equal("Do the thing.", fakeClient.LastReceivedMessages[1].Text);
        Assert.DoesNotContain("Context:", fakeClient.LastReceivedMessages[1].Text);
    }

    /// <summary>
    /// IChatClient that always throws on GetResponseAsync.
    /// </summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM connection failed");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM connection failed");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

/// <summary>
/// Fake IChatClient for SubAgentActor tests (and other test files that need it).
/// Copied from LlmSessionIntegrationTests — kept internal for cross-file reuse.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => _callCount;

    /// <summary>
    /// Snapshot of the messages passed to the most recent call. Replaced on every call.
    /// </summary>
    public IReadOnlyList<ChatMessage>? LastReceivedMessages { get; private set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// When set, the first response returns these tool calls instead of text.
    /// Subsequent calls return normal text (simulating the LLM completing after tool results).
    /// When <see cref="AlwaysReturnToolCalls"/> is true, every call returns tool calls
    /// as long as tools are available in options.
    /// </summary>
    public List<FunctionCallContent>? ToolCallsOnFirstCall { get; set; }

    /// <summary>
    /// When true, every call returns tool calls as long as options.Tools is non-empty.
    /// </summary>
    public bool AlwaysReturnToolCalls { get; set; }

    public string? ResponseText { get; set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        LastReceivedMessages = messages.ToList();

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        if (ToolCallsOnFirstCall is not null)
        {
            var returnToolCalls = AlwaysReturnToolCalls
                ? options?.Tools?.Count > 0
                : _callCount == 1;

            if (returnToolCalls)
            {
                var toolCallContents = new List<AIContent>(ToolCallsOnFirstCall);
                var toolCallMessage = new ChatMessage(
                    ChatRole.Assistant, toolCallContents);
                return new ChatResponse(toolCallMessage);
            }
        }

        var responseMessage = new ChatMessage(
            ChatRole.Assistant,
            [new TextContent(ResponseText ?? $"[fake] Response #{_callCount}")]);
        return new ChatResponse(responseMessage);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => CreateStreamingUpdatesAsync(messages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> CreateStreamingUpdatesAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class RecordingMcpToolInvoker(string result) : IMcpToolInvoker
{
    public string? ServerName { get; private set; }
    public string? ToolName { get; private set; }
    public string? SessionId { get; private set; }
    public TrustAudience? Audience { get; private set; }

    public Task<string> InvokeAsync(
        string serverName,
        string toolName,
        IDictionary<string, object?>? arguments,
        ToolExecutionContext? context,
        CancellationToken ct = default)
    {
        ServerName = serverName;
        ToolName = toolName;
        SessionId = context?.SessionId;
        Audience = context?.Audience;
        return Task.FromResult(result);
    }
}

internal sealed class RecordingParentApprovalBridge(ParentApprovalDecision decisionToReturn) : IParentApprovalBridge
{
    public int RequestCount { get; private set; }
    public List<string> RequestedPatterns { get; } = [];
    public string? RequestedCwd { get; private set; }
    public IReadOnlyList<ParentApprovalCandidate> RequestedCandidates { get; private set; } = [];
    public IReadOnlyList<ParentApprovalOption> RequestedOptions { get; private set; } = [];

    public Task<ParentApprovalDecision> RequestApprovalAsync(
        ToolCallId callId,
        string toolName,
        string displayText,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> candidateVerbs,
        IReadOnlyList<ParentApprovalCandidate> candidates,
        string? cwd,
        IReadOnlyList<ParentApprovalOption> options,
        bool isMessy,
        CancellationToken ct)
    {
        RequestCount++;
        RequestedPatterns.AddRange(patterns);
        RequestedCwd = cwd;
        RequestedCandidates = candidates;
        RequestedOptions = options;
        return Task.FromResult(decisionToReturn);
    }
}

internal sealed class SequencedToolCallChatClient(IReadOnlyList<FunctionCallContent> toolCalls) : IChatClient
{
    private int _callCount;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming path is used in subagent tests.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => CreateStreamingUpdatesAsync(cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> CreateStreamingUpdatesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _callCount++;

        ChatResponse response;
        if (_callCount <= toolCalls.Count)
        {
            response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [toolCalls[_callCount - 1]]));
        }
        else
        {
            response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [new TextContent("[fake] finished") ]));
        }

        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
