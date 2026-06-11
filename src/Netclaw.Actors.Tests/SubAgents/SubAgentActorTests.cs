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
using System.Threading.Channels;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tests.Sessions;
using ApprovalOptionKeys = Netclaw.Actors.Protocol.ApprovalOptionKeys;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

public class SubAgentActorTests : TestKit
{
    private static readonly TimeSpan ApprovalAskTimeout = TimeSpan.FromSeconds(30);

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

    private static string MalformedToolCallMarkup() => """
        <function=shell_execute>
        <parameter=Command>
        gh pr list --repo example/repo --state open
        </parameter>
        </function>
        </tool_call>
        """;

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
    public async Task Malformed_final_tool_markup_gets_one_repair_turn()
    {
        var fakeClient = new FakeChatClient
        {
            ResponseTextsByCall =
            [
                MalformedToolCallMarkup(),
                "Final report based on executed results."
            ]
        };
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Analyze repos", Timeout = TimeSpan.FromSeconds(5), Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("Final report based on executed results.", result.Output);
        Assert.Equal(2, fakeClient.CallCount);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        Assert.Contains(fakeClient.LastReceivedMessages,
            message => message.Role == ChatRole.User
                       && message.Text.Contains("unexecuted tool-call markup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeated_malformed_final_tool_markup_fails_without_timeout_error()
    {
        var fakeClient = new FakeChatClient
        {
            ResponseTextsByCall =
            [
                MalformedToolCallMarkup(),
                MalformedToolCallMarkup()
            ]
        };
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Analyze repos", Timeout = TimeSpan.FromSeconds(5), Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(2, fakeClient.CallCount);
        Assert.Contains("malformed final output", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a timeout", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fenced_tool_markup_example_is_accepted_as_final_output()
    {
        const string finalOutput = """
            The failed model output looked like this:

            ```xml
            <function=shell_execute>
            <parameter=Command>
            gh pr list --repo example/repo --state open
            </parameter>
            </function>
            </tool_call>
            ```

            No tool call was executed from that quoted example.
            """;
        var fakeClient = new FakeChatClient
        {
            ResponseTextsByCall = [finalOutput]
        };
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Explain malformed output", Timeout = TimeSpan.FromSeconds(5), Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(finalOutput, result.Output);
        Assert.Equal(1, fakeClient.CallCount);
    }

    [Theory]
    [InlineData("<function=shell_execute>\n<parameter=Command>\nls\n</parameter>\n</function>", true)]
    [InlineData("<tool_call>\n<function=shell_execute>\n</function>\n</tool_call>", true)]
    [InlineData("> <function=shell_execute>\n> <parameter=Command>\n> ls\n> </parameter>\n> </function>", false)]
    [InlineData("```xml\n<function=shell_execute>\n<parameter=Command>\nls\n</parameter>\n</function>\n```", false)]
    [InlineData("The literal token <function=shell_execute> appeared in the transcript.", false)]
    public void Tool_call_markup_detection_ignores_quoted_examples(string text, bool expected)
    {
        Assert.Equal(expected, SubAgentActor.ContainsUnexecutedToolCallMarkup(text));
    }

    [Fact]
    public async Task System_prompt_includes_headless_subagent_contract()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Say hello", Timeout = TimeSpan.FromSeconds(5), Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        Assert.Equal(ChatRole.System, fakeClient.LastReceivedMessages[0].Role);
        Assert.Contains("headless, non-interactive worker", fakeClient.LastReceivedMessages[0].Text);
        Assert.Contains("Do not ask the user clarifying questions", fakeClient.LastReceivedMessages[0].Text);
        Assert.Contains("Parent-mediated tool approval", fakeClient.LastReceivedMessages[0].Text);
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
    public async Task Tool_model_input_image_is_attached_to_subagent_followup_call()
    {
        using var dir = new DisposableTempDir();
        var imagePath = Path.Combine(dir.Path, "diagram.png");
        await File.WriteAllBytesAsync(imagePath, FakePngBytes, TestContext.Current.CancellationToken);
        var fakeTool = new FakeNetclawTool(
            "load_image",
            "image loaded",
            onExecute: context => context.AddModelInputFile(imagePath, "diagram.png", "image/png"));
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [new FunctionCallContent("call-image", "load_image")]
        };
        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Inspect the image.",
                Timeout = TimeSpan.FromSeconds(5),
                Audience = TrustAudience.Personal,
                ParentSessionDirectory = dir.Path,
                ModelInputModalities = ModelModality.Text | ModelModality.Image
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.True(fakeTool.LastContext!.ModelInputModalities.HasFlag(ModelModality.Image));
        Assert.NotNull(fakeClient.LastReceivedMessages);
        var nudge = Assert.Single(fakeClient.LastReceivedMessages!, message =>
            message.Role == ChatRole.User
            && message.Text.Contains("media", StringComparison.OrdinalIgnoreCase));
        var data = Assert.Single(nudge.Contents.OfType<DataContent>());
        Assert.Equal("image/png", data.MediaType);
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
        Assert.Contains("You are a test agent.", systemMessage.Text);
        Assert.DoesNotContain("Project rules:", systemMessage.Text);
    }

    [Fact]
    public async Task Approval_gated_tool_without_bridge_fails_subagent_without_executing_tool()
    {
        var fakeTool = new FakeNetclawTool("shell_execute", "should not run");
        var policy = CreateApprovalRequiredPolicy();
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

        Assert.False(result.Success);
        Assert.False(fakeTool.WasCalled);
        Assert.Contains("approval bridge", result.Output, StringComparison.OrdinalIgnoreCase);
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
        var policy = CreateApprovalRequiredPolicy();

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
        var policy = CreateApprovalRequiredPolicy();
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
    public async Task SubAgent_does_not_timeout_while_awaiting_human_approval()
    {
        // Regression: the sub-agent's inactivity watchdog must not abort a
        // legitimate approval wait. A slow human approver (here, ~1s for a
        // budget of 250ms) should still see the approval delivered and the
        // sub-agent complete successfully and run the approved tool retry.
        var fakeTool = new FakeNetclawTool("shell_execute", "ok");
        var policy = CreateApprovalRequiredPolicy();
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-slow-approval", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        var releaseSignal = new TaskCompletionSource<ParentApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalBridge = new DelayingParentApprovalBridge(releaseSignal.Task);

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy));

        var runTask = agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Push to origin",
                // 250ms inactivity budget — much smaller than the human delay
                // below. Before this fix, this would always abort.
                Timeout = TimeSpan.FromMilliseconds(250),
                Audience = TrustAudience.Personal,
                ApprovalBridge = approvalBridge
            },
            ApprovalAskTimeout, TestContext.Current.CancellationToken);

        // Deterministic sync: wait until the sub-agent has actually entered
        // the approval wait, then prove the run stays incomplete well past the
        // 250ms budget before releasing the human decision.
        await approvalBridge.EnteredApprovalWait.WaitAsync(TestContext.Current.CancellationToken);
        await AssertNotCompletedWithinAsync(runTask, TimeSpan.FromSeconds(1));
        releaseSignal.SetResult(ParentApprovalDecision.ApprovedOnce);

        var result = await runTask;
        Assert.True(result.Success, $"Expected success but got: {result.Output}");
        Assert.True(fakeTool.WasCalled, "Tool retry after approval did not run");
        Assert.Equal(1, approvalBridge.RequestCount);
    }

    [Fact]
    public async Task SubAgent_approval_wait_activity_suspends_parent_tool_watchdog()
    {
        var fakeTool = new FakeNetclawTool("shell_execute", "ok");
        var policy = CreateApprovalRequiredPolicy();
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-activity-approval", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        var releaseSignal = new TaskCompletionSource<ParentApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalBridge = new DelayingParentApprovalBridge(releaseSignal.Task);
        var activityChannel = Channel.CreateUnbounded<ToolActivityUpdate>();

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy));

        var runTask = agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(5),
                Audience = TrustAudience.Personal,
                ApprovalBridge = approvalBridge,
                ActivitySink = activityChannel.Writer
            },
            ApprovalAskTimeout, TestContext.Current.CancellationToken);

        await approvalBridge.EnteredApprovalWait.WaitAsync(TestContext.Current.CancellationToken);
        var waitingActivity = await ReadActivityAsync(
            activityChannel.Reader,
            "awaiting human approval",
            TestContext.Current.CancellationToken);
        Assert.True(waitingActivity.SuspendsInactivityWatchdog);

        releaseSignal.SetResult(ParentApprovalDecision.ApprovedOnce);
        var resolvedActivity = await ReadActivityAsync(
            activityChannel.Reader,
            "approval resolved",
            TestContext.Current.CancellationToken);
        Assert.False(resolvedActivity.SuspendsInactivityWatchdog);

        var result = await runTask;
        Assert.True(result.Success, $"Expected success but got: {result.Output}");
    }

    [Fact]
    public async Task SubAgent_cancels_promptly_on_external_cancellation_during_approval_wait()
    {
        // External cancellation (parent passivation, daemon restart, user
        // cancel) MUST still abort an in-flight approval wait — the watchdog
        // pause does not turn the wait uncancellable.
        var fakeTool = new FakeNetclawTool("shell_execute", "ok");
        var policy = CreateApprovalRequiredPolicy();
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-cancel", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        // Bridge holds forever — only external cancellation can unblock the
        // sub-agent.
        var neverReleased = new TaskCompletionSource<ParentApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalBridge = new DelayingParentApprovalBridge(neverReleased.Task);

        using var externalCts = new CancellationTokenSource();
        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy));

        var runTask = agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(10),
                Audience = TrustAudience.Personal,
                ApprovalBridge = approvalBridge,
                Cancellation = externalCts.Token
            },
            ApprovalAskTimeout, TestContext.Current.CancellationToken);

        // Deterministic: wait until the sub-agent is actually inside the
        // approval wait before cancelling. Without this signal the cancel can
        // race the bridge call entry and the test asserts on a window that
        // doesn't yet exist.
        await approvalBridge.EnteredApprovalWait.WaitAsync(TestContext.Current.CancellationToken);
        externalCts.Cancel();

        var result = await runTask;
        Assert.False(result.Success);
        // Match the canonical 'cancel' prefix rather than a specific spelling —
        // 'cancelled' (UK, from "Subagent cancelled by parent") vs 'canceled'
        // (US, from OperationCanceledException.Message) both flow through
        // depending on which mailbox path wins.
        Assert.Contains("cancel", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubAgent_parallel_tool_calls_each_awaiting_approval()
    {
        // Two parallel approvals in one assistant batch. The counter must hit
        // 2 then decrement back to 0; the watchdog must not fire for the
        // duration of either wait.
        var fakeTool = new FakeNetclawTool("shell_execute", "ok");
        var policy = CreateApprovalRequiredPolicy();
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-par-1", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" }),
                new FunctionCallContent("call-par-2", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        var releaseSignal = new TaskCompletionSource<ParentApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalBridge = new DelayingParentApprovalBridge(releaseSignal.Task);

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy));

        var runTask = agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Push to origin twice",
                // 200ms budget — shorter than the assertion window below, so
                // a non-paused watchdog would abort before release.
                Timeout = TimeSpan.FromMilliseconds(200),
                Audience = TrustAudience.Personal,
                ApprovalBridge = approvalBridge
            },
            ApprovalAskTimeout, TestContext.Current.CancellationToken);

        await AwaitAssertAsync(
            () => Assert.Equal(2, approvalBridge.RequestCount),
            cancellationToken: TestContext.Current.CancellationToken);
        await AssertNotCompletedWithinAsync(runTask, TimeSpan.FromMilliseconds(500));
        releaseSignal.SetResult(ParentApprovalDecision.ApprovedOnce);

        var result = await runTask;
        Assert.True(result.Success, $"Expected success but got: {result.Output}");
        Assert.Equal(2, approvalBridge.RequestCount);
    }

    [Theory]
    [InlineData(ParentApprovalDecision.Denied, "Tool access denied: approval_denied_by_user")]
    [InlineData(ParentApprovalDecision.TimedOut, "Tool access denied: approval_timed_out")]
    public async Task Rejected_approval_returns_tool_result_without_executing_tool(
        ParentApprovalDecision decision,
        string expectedToolResult)
    {
        var fakeTool = new FakeNetclawTool("shell_execute", "should not run");
        var policy = CreateApprovalRequiredPolicy();
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-rejected", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };
        var approvalBridge = new RecordingParentApprovalBridge(decision);

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(5),
                Audience = TrustAudience.Personal,
                ApprovalBridge = approvalBridge
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(fakeTool.WasCalled);
        Assert.Equal(expectedToolResult, GetLastToolResult(fakeClient, "call-rejected"));
    }

    [Fact]
    public async Task External_stop_during_approval_wait_replies_once_and_cancels_wait()
    {
        var fakeTool = new FakeNetclawTool("shell_execute", "should not run");
        var policy = CreateApprovalRequiredPolicy();
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-stop", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };
        var neverReleased = new TaskCompletionSource<ParentApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalBridge = new DelayingParentApprovalBridge(neverReleased.Task);

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy));

        var runTask = agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(10),
                Audience = TrustAudience.Personal,
                ApprovalBridge = approvalBridge
            },
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await approvalBridge.EnteredApprovalWait.WaitAsync(TestContext.Current.CancellationToken);
        agent.Tell(PoisonPill.Instance);

        var result = await runTask;
        Assert.False(result.Success);
        Assert.Contains("stopped", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(fakeTool.WasCalled);
    }

    private static ToolAccessPolicy CreateApprovalRequiredPolicy()
    {
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        return new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());
    }

    private static string? GetLastToolResult(FakeChatClient fakeClient, string callId)
    {
        Assert.NotNull(fakeClient.LastReceivedMessages);
        return fakeClient.LastReceivedMessages!
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single(r => r.CallId == callId)
            .Result?.ToString();
    }

    private static async Task<ToolActivityUpdate> ReadActivityAsync(
        ChannelReader<ToolActivityUpdate> reader,
        string phase,
        CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var activity))
            {
                if (string.Equals(activity.Phase, phase, StringComparison.Ordinal))
                    return activity;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected activity phase '{phase}'.");
    }

    private static async Task AssertNotCompletedWithinAsync(Task<SubAgentResult> task, TimeSpan duration)
    {
        try
        {
            var result = await task.WaitAsync(duration, TestContext.Current.CancellationToken);
            throw new Xunit.Sdk.XunitException(
                $"Expected sub-agent run to remain pending for {duration}, but it completed: {result.Output}");
        }
        catch (TimeoutException ex) when (ex.GetType() == typeof(TimeoutException))
        {
            return;
        }
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            definition,
            fakeClient,
            maxToolIterations: 3));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Loop forever", Timeout = TimeSpan.FromSeconds(10) , Audience = TrustAudience.Personal },
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // After the configured tool budget, force a no-tools call which returns text.
        Assert.True(result.Success);
        Assert.Equal(4, fakeClient.CallCount);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        Assert.Contains(fakeClient.LastReceivedMessages,
            message => message.Role == ChatRole.User
                       && message.Text.Contains("Start wrapping up your tool usage", StringComparison.Ordinal));
        Assert.Contains(fakeClient.LastReceivedMessages,
            message => message.Role == ChatRole.User
                       && message.Text.Contains("Do NOT request any more tools", StringComparison.Ordinal));
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
            // No first token ever arrives, so the prefill liveness budget governs;
            // set it small so the stalled call fails fast. (An unset prefill now
            // defaults to the generous 1800s session budget rather than collapsing
            // to Timeout, so Timeout alone no longer bounds the wait-for-first-token.)
            new RunSubAgent
            {
                Task = "Slow task",
                Timeout = TimeSpan.FromMilliseconds(500),
                PrefillTimeout = TimeSpan.FromMilliseconds(500),
                Audience = TrustAudience.Personal
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Findings);
    }

    // The two-phase promote/refresh policy (keepalives refresh the prefill budget;
    // the first substantive delta promotes to the inter-delta budget) is proven
    // deterministically in ProcessingWatchdogTests — no wall-clock, no Task.Delay.
    // The actor-level test below only verifies that when the watchdog timer fires
    // the sub-agent completes with the right failure; it parks the stream so the
    // real watchdog timer is the only thing that can end the call (nothing races it).

    [Fact]
    public async Task Silent_prefill_times_out_at_the_prefill_ceiling()
    {
        // The model never produces a first token (the stream parks). The prefill
        // ceiling bounds the call even though the inter-delta budget is large.
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), new ParkingChatClient()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Silent prefill",
                Timeout = TimeSpan.FromSeconds(30),         // inter-delta — not the governing budget here
                PrefillTimeout = TimeSpan.FromSeconds(2),   // the budget under test
                Audience = TrustAudience.Personal
            },
            TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_progress_deadline_kills_a_call_that_never_produces_a_token()
    {
        // The keepalive-immune deadline is the governing bound here: the liveness
        // prefill budget is generous, but the stream never produces a substantive
        // token, so the no-progress deadline fires first and reports the
        // no-substantive-output reason. (That keepalives refresh the liveness timer
        // yet never reset this deadline is proven in ProcessingWatchdogTests.)
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), new ParkingChatClient()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Wedged",
                Timeout = TimeSpan.FromSeconds(30),           // inter-delta — not governing
                PrefillTimeout = TimeSpan.FromSeconds(30),    // liveness — generous, not governing
                NoProgressTimeout = TimeSpan.FromSeconds(2),  // the budget under test
                Audience = TrustAudience.Personal
            },
            TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("no substantive output", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_distinguishes_keepalives_from_substantive_progress()
    {
        // Content-free heartbeat (e.g. prompt_progress) — a keepalive, not substantive.
        var empty = StreamingResponseReader.Classify(
            new ChatResponseUpdate { Role = ChatRole.Assistant },
            anySubstantiveSeen: false);
        Assert.False(empty.HasSubstantiveContent);
        Assert.False(empty.IsFirstSubstantive);

        // Usage-only chunk — still a keepalive (stats, no model output).
        var usageOnly = StreamingResponseReader.Classify(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(new UsageDetails())] },
            anySubstantiveSeen: false);
        Assert.False(usageOnly.HasSubstantiveContent);

        // First real text delta — substantive and first.
        var text = StreamingResponseReader.Classify(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("working")] },
            anySubstantiveSeen: false);
        Assert.True(text.HasSubstantiveContent);
        Assert.True(text.IsFirstSubstantive);

        // Tool-call content is substantive.
        var toolCall = StreamingResponseReader.Classify(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("call-1", "inspect_context")] },
            anySubstantiveSeen: false);
        Assert.True(toolCall.HasSubstantiveContent);

        // A finish-reason-only update ends the call — substantive, not a keepalive.
        var finishOnly = StreamingResponseReader.Classify(
            new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = ChatFinishReason.Stop },
            anySubstantiveSeen: false);
        Assert.True(finishOnly.HasSubstantiveContent);

        // A non-text content type (e.g. data/image, or a provider error/refusal) is
        // real output, not a heartbeat — must remain substantive so it promotes the
        // watchdog off the prefill budget.
        var nonText = StreamingResponseReader.Classify(
            new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new DataContent(new byte[] { 1, 2, 3 }, "application/octet-stream")]
            },
            anySubstantiveSeen: false);
        Assert.True(nonText.HasSubstantiveContent);

        // Substantive content after we've already seen output is not "first".
        var laterText = StreamingResponseReader.Classify(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("more")] },
            anySubstantiveSeen: true);
        Assert.True(laterText.HasSubstantiveContent);
        Assert.False(laterText.IsFirstSubstantive);
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

    // Real PNG: the egress normalizer decodes every model-input image, so a
    // fake magic-byte stub would now be dropped. Small enough to pass through.
    private static readonly byte[] FakePngBytes = TestImages.SmallPng();
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

    public IReadOnlyList<string>? ResponseTextsByCall { get; set; }

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

        var responseText = ResponseTextsByCall is { Count: > 0 } responses && _callCount <= responses.Count
            ? responses[_callCount - 1]
            : ResponseText ?? $"[fake] Response #{_callCount}";

        var responseMessage = new ChatMessage(
            ChatRole.Assistant,
            [new TextContent(responseText)]);
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

/// <summary>
/// Streaming-only fake that emits no updates and parks until the consumer cancels
/// (i.e. the sub-agent's watchdog fires). No <c>Task.Delay</c>: the only timing is
/// the real watchdog timer, which nothing races, so the watchdog behavior under
/// test (prefill liveness ceiling, no-progress deadline) is deterministic.
/// </summary>
internal sealed class ParkingChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ParkingChatClient is streaming-only.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await TestStreamingHelpers.ParkUntilCancelledAsync(cancellationToken);
        yield break;
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

/// <summary>
/// Test bridge that holds the approval decision until an external signal
/// completes. The factory variant returns a fresh task per call (for
/// parallel-approval tests where each call needs an independent wait).
/// <see cref="EnteredApprovalWait"/> signals every time the sub-agent reaches
/// the awaited bridge call, so tests can replace `await Task.Delay(...)` race
/// windows with a deterministic synchronization point.
/// </summary>
internal sealed class DelayingParentApprovalBridge : IParentApprovalBridge
{
    private readonly Func<Task<ParentApprovalDecision>> _decisionFactory;
    private readonly TaskCompletionSource<bool> _enteredSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestCount;

    public DelayingParentApprovalBridge(Task<ParentApprovalDecision> sharedTask)
        : this(() => sharedTask)
    {
    }

    public DelayingParentApprovalBridge(Func<Task<ParentApprovalDecision>> decisionFactory)
    {
        _decisionFactory = decisionFactory;
    }

    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>
    /// Completes the first time <see cref="RequestApprovalAsync"/> is entered.
    /// Tests should `await EnteredApprovalWait` before cancelling or releasing
    /// the approval, so the synchronization window is deterministic rather
    /// than a real-time sleep.
    /// </summary>
    public Task EnteredApprovalWait => _enteredSignal.Task;

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
        Interlocked.Increment(ref _requestCount);
        _enteredSignal.TrySetResult(true);
        // Task.WaitAsync(CancellationToken) throws OperationCanceledException on
        // cancel and observes faults on the underlying task — replaces a
        // hand-rolled WhenAny+Register+TCS dance.
        return _decisionFactory().WaitAsync(ct);
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
