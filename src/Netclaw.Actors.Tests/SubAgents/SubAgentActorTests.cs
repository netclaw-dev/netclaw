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
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tests.Sessions;
using ApprovalOptionKeys = Netclaw.Actors.Protocol.ApprovalOptionKeys;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using FakeChatClient = Netclaw.Tests.Utilities.FakeChatClient;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

public class SubAgentActorTests : TestKit
{
    private static readonly TimeSpan ApprovalAskTimeout = TimeSpan.FromSeconds(30);
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static FunctionCallContent CreateToolCall(string callId, string name)
        => CreateToolCall(callId, name, new Dictionary<string, object?>());

    private static FunctionCallContent CreateToolCall(
        string callId,
        string name,
        IDictionary<string, object?> arguments)
    {
        var callArguments = new Dictionary<string, object?>(arguments, StringComparer.Ordinal)
        {
            ["_rationale"] = "Verify the sub-agent behavior."
        };
        return new FunctionCallContent(callId, name, callArguments);
    }

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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Say hello", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Completed, result.Outcome);
        Assert.Null(result.OutcomeReason);
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Analyze repos", Timeout = TimeSpan.FromSeconds(5) },
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Analyze repos", Timeout = TimeSpan.FromSeconds(5) },
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Explain malformed output", Timeout = TimeSpan.FromSeconds(5) },
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
        var scopeTool = new FakeNetclawTool(SetWorkingDirectoryTool.ToolName, "ok");
        var definition = CreateDefinition([scopeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Say hello", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        Assert.Equal(ChatRole.System, fakeClient.LastReceivedMessages[0].Role);
        Assert.Contains("headless, non-interactive worker", fakeClient.LastReceivedMessages[0].Text);
        Assert.Contains("subagent role guidance and assigned task are more specific", fakeClient.LastReceivedMessages[0].Text);
        Assert.Contains("safety, security, trust-boundary, approval, and tool-policy rules remain mandatory", fakeClient.LastReceivedMessages[0].Text);
        Assert.Contains("Do not ask the user clarifying questions", fakeClient.LastReceivedMessages[0].Text);
        Assert.Contains("Parent-mediated tool approval", fakeClient.LastReceivedMessages[0].Text);
        Assert.Contains("Before tool work in another task-named project", fakeClient.LastReceivedMessages[0].Text);
        Assert.Contains("Declare the task's first project path exactly", fakeClient.LastReceivedMessages[0].Text);
    }

    [Fact]
    public async Task System_prompt_omits_project_declaration_when_scope_tool_is_unavailable()
    {
        var fakeClient = new FakeChatClient();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition(),
            fakeClient,
            PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Say hello",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        Assert.DoesNotContain(
            "call set_working_directory once",
            fakeClient.LastReceivedMessages[0].Text);
    }

    [Fact]
    public async Task Public_system_prompt_omits_project_declaration_even_when_definition_contains_scope_tool()
    {
        var fakeClient = new FakeChatClient();
        var scopeTool = new FakeNetclawTool(SetWorkingDirectoryTool.ToolName, "ok");
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([scopeTool]),
            fakeClient,
            PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(audience: TrustAudience.Public),
                Task = "Say hello",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        Assert.DoesNotContain(
            "call set_working_directory once",
            fakeClient.LastReceivedMessages[0].Text);
    }

    [Fact]
    public async Task System_prompt_layers_operating_rules_before_project_role_and_headless_contract()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition() with
        {
            OperatingRules = "Operating rules: never invent runtime facts.\n\nDeployment playbook: review customer email.",
            ProjectInstructions = "Project rules: prefer C#.",
            SystemPrompt = "You are a test agent.\n\n[Skill Overlay]\nUse focused analysis."
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Do the thing.", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        var systemPrompt = fakeClient.LastReceivedMessages!.Single(m => m.Role == ChatRole.System).Text;

        AssertPromptOrder(
            systemPrompt,
            "Operating rules: never invent runtime facts.",
            "Deployment playbook: review customer email.",
            "Project rules: prefer C#.",
            "You are a test agent.",
            "[Skill Overlay]",
            "[Subagent Execution Contract]");
        Assert.Contains(
            "Return each authorized file path that the parent session should deliver.",
            systemPrompt,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "Always end by emitting a final output for the parent session.",
            systemPrompt.TrimEnd(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_call_executes_and_continues()
    {
        var fakeTool = new FakeNetclawTool("greet", "Hello from tool!");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-1", "greet",
                    new Dictionary<string, object?> { ["name"] = "World" })
            ]
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Greet the user", Timeout = TimeSpan.FromSeconds(5) },
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
            ToolCallsOnFirstCall = [CreateToolCall("call-image", "load_image")]
        };
        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: dir.Path,
                    modelInputModalities: ModelModality.Text | ModelModality.Image),
                Task = "Inspect the image.",
                Timeout = TimeSpan.FromSeconds(5)
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
                CreateToolCall("call-context", "inspect_context")
            ]
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: "/tmp/netclaw/sessions/abc",
                    projectDirectory: "/home/user/workspaces/netclaw",
                    recentFiles: ["src/Netclaw.Actors/SubAgents/SubAgentActor.cs"]),
                Task = "Inspect the inherited paths.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.Equal("/tmp/netclaw/sessions/abc", fakeTool.LastContext!.SessionDirectory);
        Assert.Equal("/home/user/workspaces/netclaw", fakeTool.LastContext.ProjectDirectory);
        Assert.Equal(["src/Netclaw.Actors/SubAgents/SubAgentActor.cs"], fakeTool.LastContext.RecentFiles);
    }

    [Fact]
    public async Task Tool_execution_with_no_parent_project_directory_passes_null_through()
    {
        var fakeTool = new FakeNetclawTool("inspect_context", "ok");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [CreateToolCall("call-no-project", "inspect_context")]
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(sessionDirectory: "/tmp/netclaw/sessions/xyz"),
                Task = "Inspect inherited paths.",
                Timeout = TimeSpan.FromSeconds(5)
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
            ToolCallsOnFirstCall = [CreateToolCall("call-cwd", "inspect_context")]
        };

        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([fakeTool]), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: "/tmp/netclaw/sessions/parent",
                    projectDirectory: "/home/user/repos/foo",
                    inheritedCwd: "/home/user/repos/foo"),
                Task = "Inspect inherited cwd.",
                Timeout = TimeSpan.FromSeconds(5)
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
            ToolCallsOnFirstCall = [CreateToolCall("call-null-cwd", "inspect_context")]
        };

        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([fakeTool]), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Inspect null cwd.",
                Timeout = TimeSpan.FromSeconds(5)
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
            ToolCallsOnFirstCall = [CreateToolCall("call-inherit-only", "inspect_context")]
        };

        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([fakeTool]), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(inheritedCwd: "/home/user/repos/foo"),
                Task = "Inspect inherited cwd with no other sources.",
                Timeout = TimeSpan.FromSeconds(5)
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
            ToolCallsOnFirstCall = [CreateToolCall("call-1", "inspect_context")]
        };
        var firstAgent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([firstTool]), firstClient, PermissivePolicy()));

        var firstResult = await firstAgent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(projectDirectory: "/home/user/workspaces/project-a"),
                Task = "First run.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(firstResult.Success);
        Assert.Equal("/home/user/workspaces/project-a", firstTool.LastContext!.ProjectDirectory);

        var secondTool = new FakeNetclawTool("inspect_context", "ok");
        var secondClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [CreateToolCall("call-2", "inspect_context")]
        };
        var secondAgent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([secondTool]), secondClient, PermissivePolicy()));

        var secondResult = await secondAgent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(projectDirectory: "/home/user/workspaces/project-b"),
                Task = "Second run after parent project switch.",
                Timeout = TimeSpan.FromSeconds(5)
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Do the thing.", Timeout = TimeSpan.FromSeconds(5) },
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Do the thing.", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        var systemMessage = fakeClient.LastReceivedMessages!.Single(m => m.Role == ChatRole.System);
        Assert.Contains("You are a test agent.", systemMessage.Text);
        Assert.DoesNotContain("Project rules:", systemMessage.Text);
    }

    [Fact]
    public async Task Session_scratch_context_does_not_authorize_headless_prompt_worthy_shell()
    {
        using var netclawHome = new DisposableTempDir();
        var sessionDirectory = Path.Combine(netclawHome.Path, "sessions", "example");
        var fakeTool = new FakeNetclawTool("shell_execute", "should not run");
        var policy = CreateApprovalRequiredPolicy(netclawHome.Path);
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-approval", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy, approvalService: null));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(sessionDirectory: sessionDirectory),
                Task = "Try the shell tool",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(fakeTool.WasCalled);
        Assert.Contains("approval bridge", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"session_dir: {sessionDirectory}", fakeClient.LastReceivedMessages![1].Text);
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
                CreateToolCall("call-cwd-prompt", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        var approvalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce);
        var logger = new AuthorizationRecordingLogger();
        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreatePropsWithProjectInstructionProvider(
            definition,
            fakeClient,
            policy,
            NullSystemPromptProvider.Instance,
            toolExecutorLogger: logger));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: "/tmp/netclaw/sessions/parent",
                    projectDirectory: "/home/user/repos/foo",
                    inheritedCwd: "/home/user/repos/foo",
                    approvalBridge: approvalBridge),
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, approvalBridge.RequestCount);
        var authorizationAttemptId = Assert.Single(approvalBridge.AuthorizationAttemptIds);
        Assert.True(AuthorizationAttemptId.TryParse(authorizationAttemptId.Value, out _));
        Assert.NotEmpty(logger.AuthorizationAttemptIds);
        Assert.All(
            logger.AuthorizationAttemptIds,
            loggedAttemptId => Assert.Equal(authorizationAttemptId.Value, loggedAttemptId));
        Assert.Equal("/home/user/repos/foo", approvalBridge.RequestedCwd);
        Assert.Single(approvalBridge.RequestedCandidates);
        Assert.Equal("git push origin main", approvalBridge.RequestedCandidates[0].Verb);
        Assert.Contains(approvalBridge.RequestedOptions, o => o.Key == ApprovalOptionKeys.ApproveEverywhere);
        Assert.Contains(approvalBridge.RequestedOptions, o => o.Key == ApprovalOptionKeys.ApproveAlways);
        Assert.Contains(approvalBridge.RequestedOptions, o => o.Key == ApprovalOptionKeys.ApproveSession);
    }

    [Fact]
    public async Task Subagent_platform_temp_call_receives_scratch_correction_before_parent_bridge()
    {
        var fakeTool = new FakeNetclawTool(ShellTool.ToolName, "should not run");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                ScratchCall("call-scratch-correction")
            ]
        };
        var approvalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([fakeTool]),
            fakeClient,
            CreateScratchCorrectionPolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: "/home/user/.netclaw/sessions/example",
                    approvalBridge: approvalBridge),
                Task = "Inspect a disposable diagnostic artifact.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Output);
        Assert.False(fakeTool.WasCalled);
        Assert.Equal(0, approvalBridge.RequestCount);
        Assert.Equal(
            "Tool execution deferred: shared_temporary_directory\n" +
            "Session scratch directory: '/home/user/.netclaw/sessions/example'.\n" +
            "Next action: use the session scratch directory from this result for disposable files, or retry unchanged for exact platform paths.",
            GetLastToolResult(fakeClient, "call-scratch-correction"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Subagent_reviewed_safe_external_cwd_receives_project_scope_correction_before_bridge(
        bool supportsApproval)
    {
        const string callId = "call-project-scope-correction";
        var approvalBridge = supportsApproval
            ? new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce)
            : null;
        var scenario = await RunProjectScopeScenarioAsync(
            callId,
            includeScopeTool: true,
            scopeToolAccepts: true,
            approvalBridge);

        Assert.True(scenario.Result.Success, scenario.Result.Output);
        Assert.False(scenario.Shell.WasCalled);
        Assert.Equal(0, approvalBridge?.RequestCount ?? 0);
        var correction = GetLastToolResult(scenario.Client, callId);
        Assert.Equal(
            "Tool execution deferred: working_directory_not_declared\n" +
            $"Project directory: '{scenario.Worktree}'.\n" +
            "Next action: call set_working_directory with an allowed project directory for this task, then retry the failed tool call.",
            correction);
        var preservedCall = scenario.Client.LastReceivedMessages!
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .Single(call => call.CallId == callId);
        Assert.Equal(ProjectScopeCommand, preservedCall.Arguments!["Command"]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Subagent_unavailable_project_scope_keeps_parent_approval_bridge(
        bool includeScopeTool,
        bool scopeToolAccepts)
    {
        const string callId = "call-project-scope-approval";
        var approvalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce);
        var scenario = await RunProjectScopeScenarioAsync(
            callId,
            includeScopeTool,
            scopeToolAccepts,
            approvalBridge);

        Assert.True(scenario.Result.Success, scenario.Result.Output);
        Assert.True(scenario.Shell.WasCalled);
        Assert.Equal(1, approvalBridge.RequestCount);
        Assert.Equal(scenario.Worktree, approvalBridge.RequestedCwd);
        Assert.DoesNotContain(
            "working_directory_not_declared",
            GetLastToolResult(scenario.Client, callId),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subagent_policy_hidden_project_scope_tool_is_not_revealed()
    {
        const string callId = "call-project-scope-hidden";
        var approvalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce);
        var scenario = await RunProjectScopeScenarioAsync(
            callId,
            includeScopeTool: true,
            scopeToolAccepts: true,
            approvalBridge,
            hideScopeTool: true);

        Assert.True(scenario.Result.Success, scenario.Result.Output);
        Assert.True(scenario.Shell.WasCalled);
        Assert.Equal(1, approvalBridge.RequestCount);
        Assert.DoesNotContain(
            SetWorkingDirectoryTool.ToolName,
            GetLastToolResult(scenario.Client, callId),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Subagent_project_declaration_updates_child_prompt_before_unchanged_retry(
        bool supportsApproval)
    {
        const string firstCallId = "call-project-scope-first";
        const string declarationCallId = "call-project-scope-declare";
        const string retryCallId = "call-project-scope-retry";
        const string projectGuidance = "Project instructions: use the local test conventions.";
        const string sessionDirectory = "/home/user/.netclaw/sessions/project-scope-child";
        var worktree = Path.GetFullPath(AppContext.BaseDirectory);
        var shell = new FakeNetclawTool(ShellTool.ToolName, "inspected");
        var setWorkingDirectory = new SetWorkingDirectoryTool(
            new ToolConfig(),
            new NetclawPaths(worktree, worktree));
        var client = new SequencedToolCallChatClient(
        [
            ProjectScopeCall(firstCallId, worktree),
            new FunctionCallContent(
                declarationCallId,
                SetWorkingDirectoryTool.ToolName,
                new Dictionary<string, object?>
                {
                    ["Path"] = worktree,
                    ["_rationale"] = "Declare the project directory before the next inspection."
                }),
            ProjectScopeCall(retryCallId, worktree)
        ]);
        var approvalBridge = supportsApproval
            ? new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce)
            : null;
        var actor = Sys.ActorOf(SubAgentActor.CreatePropsWithProjectInstructionProvider(
            CreateDefinition([shell, setWorkingDirectory]),
            client,
            CreateProjectScopeCorrectionPolicy(worktree),
            new ProjectPromptProvider(worktree, projectGuidance)));

        var result = await actor.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: sessionDirectory,
                    approvalBridge: approvalBridge),
                Task = "Declare the project and retry the exact inspection.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        Assert.Equal(supportsApproval, result.Success);
        Assert.Equal(supportsApproval, shell.WasCalled);
        Assert.Equal(0, approvalBridge?.RequestCount ?? 0);
        Assert.Contains(
            projectGuidance,
            client.LastReceivedMessages!.Single(message => message.Role == ChatRole.System).Text,
            StringComparison.Ordinal);
        Assert.Single(
            client.LastReceivedMessages!,
            message => message.Text.Contains($"session_dir: {sessionDirectory}", StringComparison.Ordinal));
        Assert.Single(
            client.LastReceivedMessages!,
            message => message.Text.Contains(ToolChoiceGuidance.StructuredWorkspaceSelection, StringComparison.Ordinal));
        Assert.Single(
            client.LastReceivedMessages!,
            message => message.Text.Contains(ToolChoiceGuidance.DirectorySelectionOrder, StringComparison.Ordinal));
        Assert.Single(
            client.LastReceivedMessages!,
            message => message.Text.Contains(ToolChoiceGuidance.ShellCompositionOrder, StringComparison.Ordinal));
        Assert.DoesNotContain(
            sessionDirectory,
            client.LastReceivedMessages!.Single(message => message.Role == ChatRole.System).Text,
            StringComparison.Ordinal);

        if (supportsApproval)
        {
            Assert.Equal(worktree, result.WorkingContext!.ProjectDirectory);
            var parent = new WorkingContext { ProjectDirectory = "/parent/project" };
            var merged = LlmSessionActor.MergeSuccessfulSubAgentWorkingContext(parent, result.Completion);
            Assert.Equal("/parent/project", merged.ProjectDirectory);
        }
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\r")]
    [InlineData("\n")]
    public async Task Subagent_rejects_control_characters_from_project_scope_result(string controlCharacter)
    {
        const string projectGuidance = "Project instructions that must not load.";
        var worktree = Path.GetFullPath(AppContext.BaseDirectory);
        var controlledDirectory = Path.Combine(worktree, $"project-{controlCharacter}-candidate");
        var setWorkingDirectory = new SetWorkingDirectoryTool(
            new ToolConfig(),
            new NetclawPaths(worktree, worktree));
        var client = new SequencedToolCallChatClient(
        [
            new FunctionCallContent(
                "call-control-project",
                SetWorkingDirectoryTool.ToolName,
                new Dictionary<string, object?>
                {
                    ["Path"] = controlledDirectory,
                    ["_rationale"] = "Verify that the project scope rejects control characters."
                })
        ]);
        var promptProvider = new ProjectPromptProvider(controlledDirectory, projectGuidance);
        var actor = Sys.ActorOf(SubAgentActor.CreatePropsWithProjectInstructionProvider(
            CreateDefinition([setWorkingDirectory]),
            client,
            PermissivePolicy(),
            promptProvider));

        var result = await actor.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(projectDirectory: worktree),
                Task = "Try to declare the project.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Output);
        Assert.Equal(worktree, result.WorkingContext!.ProjectDirectory);
        Assert.Equal(
            "Error: path contains an invalid control character.",
            GetLastToolResult(client.LastReceivedMessages, "call-control-project"));
        Assert.DoesNotContain(controlledDirectory, result.Output, StringComparison.Ordinal);
        Assert.Equal(0, promptProvider.CallCount);
        Assert.DoesNotContain(
            projectGuidance,
            client.LastReceivedMessages!.Single(message => message.Role == ChatRole.System).Text,
            StringComparison.Ordinal);
        Assert.Contains(
            $"project_dir: {worktree}",
            client.LastReceivedMessages!.Single(message => message.Role == ChatRole.User).Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            controlledDirectory,
            client.LastReceivedMessages!.Single(message => message.Role == ChatRole.User).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subagent_parallel_temp_calls_both_receive_first_attempt_corrections()
    {
        var fakeTool = new FakeNetclawTool(ShellTool.ToolName, "should not run");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                ScratchCall("call-scratch-parallel-1"),
                ScratchCall("call-scratch-parallel-2")
            ]
        };
        var approvalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([fakeTool]),
            fakeClient,
            CreateScratchCorrectionPolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: "/home/user/.netclaw/sessions/example",
                    approvalBridge: approvalBridge),
                Task = "Inspect two disposable diagnostic artifacts.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Output);
        Assert.False(fakeTool.WasCalled);
        Assert.Equal(0, approvalBridge.RequestCount);
        Assert.Contains(
            "shared_temporary_directory",
            GetLastToolResult(fakeClient, "call-scratch-parallel-1"));
        Assert.Contains(
            "shared_temporary_directory",
            GetLastToolResult(fakeClient, "call-scratch-parallel-2"));
    }

    [Fact]
    public async Task Subagent_exact_temp_retry_reaches_once_or_deny_parent_bridge()
    {
        var fakeTool = new FakeNetclawTool(ShellTool.ToolName, "should not run");
        var fakeClient = new SequencedToolCallChatClient(
        [
            ScratchCall("call-scratch-first"),
            ScratchCall("call-scratch-retry")
        ]);
        var approvalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.Denied);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([fakeTool]),
            fakeClient,
            CreateScratchCorrectionPolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: "/home/user/.netclaw/sessions/example",
                    approvalBridge: approvalBridge),
                Task = "Retry the exact disposable diagnostic call if corrected.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Output);
        Assert.False(fakeTool.WasCalled);
        Assert.Equal(1, approvalBridge.RequestCount);
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            approvalBridge.RequestedOptions.Select(option => option.Key));
        var denial = GetLastToolResult(fakeClient.LastReceivedMessages, "call-scratch-retry");
        Assert.Contains("approval_denied_by_user", denial, StringComparison.Ordinal);
        Assert.Contains("/home/user/.netclaw/sessions/example", denial, StringComparison.Ordinal);
        Assert.DoesNotContain("set_working_directory", denial, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_once_does_not_leak_between_subagent_tool_calls()
    {
        var fakeTool = new FakeNetclawTool("shell_execute", "ok");
        var policy = CreateApprovalRequiredPolicy();
        var fakeClient = new SequencedToolCallChatClient(
            [
                CreateToolCall(
                    "call-approval-1",
                    "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" }),
                CreateToolCall(
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
                Scope = SubAgentTestScope.Create(approvalBridge: approvalBridge),
                Task = "Run the same approval-gated tool twice",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout, TestContext.Current.CancellationToken);

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
                CreateToolCall("call-slow-approval", "shell_execute",
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
                Scope = SubAgentTestScope.Create(approvalBridge: approvalBridge),
                Task = "Push to origin",
                // 250ms inactivity budget — much smaller than the human delay
                // below. Before this fix, this would always abort.
                Timeout = TimeSpan.FromMilliseconds(250)
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
    public async Task SubAgent_surfaces_approval_wait_and_resolution_to_parent_stream()
    {
        var fakeTool = new FakeNetclawTool("shell_execute", "ok");
        var policy = CreateApprovalRequiredPolicy();
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-activity-approval", "shell_execute",
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
                Scope = SubAgentTestScope.Create(approvalBridge: approvalBridge),
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(5),
                ActivitySink = activityChannel.Writer
            },
            ApprovalAskTimeout, TestContext.Current.CancellationToken);

        // The sub-agent surfaces its approval state to the parent stream so the run
        // stays visible while a human is in the loop. (Pausing the parent watchdog
        // is no longer a flag on the activity — the parent no longer wall-clock-
        // supervises a self-monitoring sub-agent.)
        await approvalBridge.EnteredApprovalWait.WaitAsync(TestContext.Current.CancellationToken);
        await ReadActivityAsync(
            activityChannel.Reader,
            "awaiting human approval",
            TestContext.Current.CancellationToken);

        releaseSignal.SetResult(ParentApprovalDecision.ApprovedOnce);
        await ReadActivityAsync(
            activityChannel.Reader,
            "approval resolved",
            TestContext.Current.CancellationToken);

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
                CreateToolCall("call-cancel", "shell_execute",
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
                Scope = SubAgentTestScope.Create(approvalBridge: approvalBridge),
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(10),
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
                CreateToolCall("call-par-1", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" }),
                CreateToolCall("call-par-2", "shell_execute",
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
                Scope = SubAgentTestScope.Create(approvalBridge: approvalBridge),
                Task = "Push to origin twice",
                // 200ms budget — shorter than the assertion window below, so
                // a non-paused watchdog would abort before release.
                Timeout = TimeSpan.FromMilliseconds(200)
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
                CreateToolCall("call-rejected", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };
        var approvalBridge = new RecordingParentApprovalBridge(decision);

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(approvalBridge: approvalBridge),
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(5)
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
                CreateToolCall("call-stop", "shell_execute",
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
                Scope = SubAgentTestScope.Create(approvalBridge: approvalBridge),
                Task = "Push to origin",
                Timeout = TimeSpan.FromSeconds(10)
            },
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await approvalBridge.EnteredApprovalWait.WaitAsync(TestContext.Current.CancellationToken);
        agent.Tell(PoisonPill.Instance);

        var result = await runTask;
        Assert.False(result.Success);
        Assert.Contains("stopped", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(fakeTool.WasCalled);
    }

    private static ToolAccessPolicy PermissivePolicy() => new(
        new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
        new EffectivePolicyDefaults(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false),
        new ShellCommandPolicy(),
        new ToolPathPolicy([]));

    private static ToolAccessPolicy CreateApprovalRequiredPolicy(string? netclawHome = null)
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
            new ShellCommandPolicy(),
            new ToolPathPolicy([]),
            shellTrustZonePolicy: netclawHome is null
                ? null
                : new ShellTrustZonePolicy(toolConfig, new NetclawPaths(netclawHome)));
    }

    private static ToolAccessPolicy CreateScratchCorrectionPolicy()
    {
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [ShellTool.ToolName] = ToolApprovalMode.Approval
            }
        };
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var commandPolicy = new ShellCommandPolicy(environment);
        var pathPolicy = new ToolPathPolicy(environment, []);
        return new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            pathPolicy,
            new PlatformTemporaryScopePolicy(
                environment,
                "/tmp",
                new AlwaysSafeTemporaryPathInspector()));
    }

    private async Task<ProjectScopeScenario> RunProjectScopeScenarioAsync(
        string callId,
        bool includeScopeTool,
        bool scopeToolAccepts,
        IParentApprovalBridge? approvalBridge,
        bool hideScopeTool = false)
    {
        var worktree = Path.GetFullPath(AppContext.BaseDirectory);
        var shell = new FakeNetclawTool(ShellTool.ToolName, "approved");
        var tools = new List<INetclawTool> { shell };
        if (includeScopeTool)
        {
            var allowedRoot = scopeToolAccepts
                ? worktree
                : Path.Combine(worktree, "different-workspace-root");
            tools.Add(new SetWorkingDirectoryTool(
                new ToolConfig(),
                new NetclawPaths(allowedRoot, allowedRoot)));
        }

        var client = new FakeChatClient
        {
            ToolCallsOnFirstCall = [ProjectScopeCall(callId, worktree)]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition(tools),
            client,
            CreateProjectScopeCorrectionPolicy(worktree, hideScopeTool)));
        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(approvalBridge: approvalBridge),
                Task = "Inspect project metrics.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        return new ProjectScopeScenario(result, shell, client, worktree);
    }

    private sealed record ProjectScopeScenario(
        SubAgentResult Result,
        FakeNetclawTool Shell,
        FakeChatClient Client,
        string Worktree);

    private sealed class ProjectPromptProvider(
        string expectedProjectDirectory,
        string projectInstructions) : ISystemPromptProvider
    {
        public int CallCount { get; private set; }

        public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null)
            => string.Empty;

        public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory)
        {
            CallCount++;
            return string.Equals(projectDirectory, expectedProjectDirectory, StringComparison.Ordinal)
                ? projectInstructions
                : null;
        }

        public string? GetOperatingRules(TrustAudience audience) => null;
    }

    private static ToolAccessPolicy CreateProjectScopeCorrectionPolicy(
        string workspacesDirectory,
        bool hideScopeTool = false)
    {
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [ShellTool.ToolName] = ToolApprovalMode.Approval,
                [SetWorkingDirectoryTool.ToolName] = hideScopeTool
                    ? ToolApprovalMode.Deny
                    : ToolApprovalMode.Auto
            }
        };
        var environment = TestShellEnvironment.Current;
        var approvalShell = environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        var safeVerbs = environment.Grammar == ShellGrammar.Bash
            ? new[] { "pwd", "whoami" }
            : ["Get-Location", "Get-Date"];
        return new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(environment),
            new ToolPathPolicy(environment, []),
            shellTrustZonePolicy: new ShellTrustZonePolicy(
                toolConfig,
                new NetclawPaths(workspacesDirectory, workspacesDirectory)),
            safeVerbs: SafeVerbList.FromVerbs(approvalShell, safeVerbs));
    }

    private static FunctionCallContent ScratchCall(string callId)
        => new(callId, ShellTool.ToolName, new Dictionary<string, object?>
        {
            ["Command"] = "gh api repos/example/project",
            ["WorkingDirectory"] = "/tmp",
            ["_rationale"] = "Inspect a disposable diagnostic artifact."
        });

    private static FunctionCallContent ProjectScopeCall(string callId, string workingDirectory)
        => new(callId, ShellTool.ToolName, new Dictionary<string, object?>
        {
            ["Command"] = ProjectScopeCommand,
            ["WorkingDirectory"] = workingDirectory,
            ["_rationale"] = "Inspect the project metric sources."
        });

    private static string ProjectScopeCommand =>
        TestShellEnvironment.Current.Grammar == ShellGrammar.Bash
            ? "pwd; whoami"
            : "Get-Location; Get-Date";

    private static string? GetLastToolResult(FakeChatClient fakeClient, string callId)
    {
        Assert.NotNull(fakeClient.LastReceivedMessages);
        return GetLastToolResult(fakeClient.LastReceivedMessages, callId);
    }

    private static string? GetLastToolResult(
        IReadOnlyList<ChatMessage>? messages,
        string callId)
    {
        Assert.NotNull(messages);
        return messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single(r => r.CallId == callId)
            .Result?.ToString();
    }

    private sealed class AlwaysSafeTemporaryPathInspector : IPlatformTemporaryPathInspector
    {
        public bool TryResolveRoot(
            string path,
            ShellPathStyle pathStyle,
            out string resolvedRoot)
            => ShellPathRules.TryNormalize(path, pathStyle, out resolvedRoot);

        public bool IsSafeDescendant(string root, string path, ShellPathStyle pathStyle)
            => true;

        public bool ContainsInvalidPathState(string path, ShellPathStyle pathStyle)
            => false;
    }

    private static void AssertPromptOrder(string prompt, params string[] markers)
    {
        var previousIndex = -1;
        foreach (var marker in markers)
        {
            var index = prompt.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected prompt to contain marker: {marker}");
            Assert.True(index > previousIndex, $"Expected marker '{marker}' to appear after the previous marker.");
            previousIndex = index;
        }
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
                CreateToolCall("call-loop", "looper")
            ],
            AlwaysReturnToolCalls = true
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            definition,
            fakeClient,
            PermissivePolicy(),
            maxToolIterations: 3));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Loop forever", Timeout = TimeSpan.FromSeconds(10) },
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // After the configured tool budget, force a no-tools call which returns text.
        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Partial, result.Outcome);
        Assert.Equal(SubAgentOutcomeReason.ToolIterationBudgetExhausted, result.OutcomeReason);
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
    public async Task Exact_tool_cycle_gets_one_correction_then_stops()
    {
        var executionCount = 0;
        var fakeTool = new FakeNetclawTool(
            "mutate_state",
            "loop result",
            onExecute: _ => Interlocked.Increment(ref executionCount));
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-loop", "mutate_state")
            ],
            AlwaysReturnToolCalls = true,
            ResponseTextsByCall =
            [
                "unused",
                "unused",
                "unused",
                "unused",
                string.Empty,
                "Final partial report."
            ]
        };

        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([fakeTool]),
            fakeClient,
            PermissivePolicy(),
            maxToolIterations: 10));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Repeat the same tool.",
                Timeout = TimeSpan.FromSeconds(10)
            },
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Partial, result.Outcome);
        Assert.Equal(SubAgentOutcomeReason.ToolCycleStopped, result.OutcomeReason);
        Assert.Equal(6, fakeClient.CallCount);
        Assert.Equal(2, executionCount);
        Assert.Equal("Final partial report.", result.Output);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        var toolResults = fakeClient.LastReceivedMessages
            .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Select(static toolResult => toolResult.Result?.ToString() ?? string.Empty)
            .ToList();
        Assert.Equal(2, toolResults.Count(static text => text == "loop result"));
        Assert.Single(toolResults, static text =>
            text.Contains("repeated action-and-outcome cycle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Timeout_returns_failure()
    {
        var fakeClient = new FakeChatClient
        {
            Delay = TimeSpan.FromSeconds(30) // Much longer than timeout
        };
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            // No first token ever arrives, so the prefill liveness budget governs;
            // set it small so the stalled call fails fast. (An unset prefill now
            // defaults to the generous 1800s session budget rather than collapsing
            // to Timeout, so Timeout alone no longer bounds the wait-for-first-token.)
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Slow task",
                Timeout = TimeSpan.FromMilliseconds(500),
                PrefillTimeout = TimeSpan.FromMilliseconds(500)
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), new ParkingChatClient(), PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Silent prefill",
                Timeout = TimeSpan.FromSeconds(30),         // inter-delta — not the governing budget here
                PrefillTimeout = TimeSpan.FromSeconds(2)    // the budget under test
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), new ParkingChatClient(), PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Wedged",
                Timeout = TimeSpan.FromSeconds(30),           // inter-delta — not governing
                PrefillTimeout = TimeSpan.FromSeconds(30),    // liveness — generous, not governing
                NoProgressTimeout = TimeSpan.FromSeconds(2)   // the budget under test
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
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [CreateToolCall("call-1", "inspect_context")] },
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
        var throwingClient = new FakeChatClient { Failure = new InvalidOperationException("LLM connection failed") };
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, throwingClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Fail", Timeout = TimeSpan.FromSeconds(5) },
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));
        Watch(agent);

        agent.Tell(new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Done", Timeout = TimeSpan.FromSeconds(5) });

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
                CreateToolCall(
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
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));

        var definition = CreateDefinition([fakePlaywrightTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy, approvalService: null));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    audience: TrustAudience.Team,
                    scopeId: "session/subagent-scope"),
                Task = "Open example.com",
                Timeout = TimeSpan.FromSeconds(5)
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
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Summarize research", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Long_text_response_emits_untruncated_findings_when_enabled()
    {
        var longSummary = string.Concat(
            new string('a', 1900),
            "\nTAIL_CONCLUSION: preserve this final conclusion and citation.");
        var fakeClient = new FakeChatClient
        {
            ResponseText = longSummary
        };
        var definition = CreateDefinition() with { EmitStructuredFindings = true };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Summarize research", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Single(result.Findings);
        Assert.Equal(longSummary, result.Findings[0].Content);
        Assert.Contains("TAIL_CONCLUSION", result.Findings[0].Content, StringComparison.Ordinal);
        Assert.Equal(SubAgentFindingShape.Conclusion, result.Findings[0].Shape);
        Assert.Equal("subagent:test-agent", result.Findings[0].Title);
        Assert.Equal(SubAgentFindingDurability.Durable, result.Findings[0].Durability);
        Assert.Equal(SubAgentFindingReusability.Reusable, result.Findings[0].Reusability);
        Assert.Equal(SubAgentFindingRecallMode.Searchable, result.Findings[0].RecallMode);
        Assert.Contains("subagent_outcome:completed", result.Findings[0].Evidence);
    }

    [Fact]
    public async Task RuntimeContext_is_prefixed_onto_first_user_message()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Summarize the recent commits.",
                RuntimeContext = "Workspace is netclaw on branch feature/foo.",
                Timeout = TimeSpan.FromSeconds(5)
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

    [Theory]
    [InlineData(TrustAudience.Personal)]
    [InlineData(TrustAudience.Team)]
    public async Task Eligible_subagent_context_announces_exact_private_session_scratch(
        TrustAudience audience)
    {
        const string sessionDirectory = "/home/user/.netclaw/sessions/example";
        var fakeClient = new FakeChatClient();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition(),
            fakeClient,
            PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    audience: audience,
                    sessionDirectory: sessionDirectory),
                Task = "Create a disposable diagnostic artifact.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var userMessage = fakeClient.LastReceivedMessages![1].Text;
        Assert.Contains("[session]", userMessage);
        Assert.Contains($"session_dir: {sessionDirectory}", userMessage);
        Assert.Contains(ToolChoiceGuidance.StructuredWorkspaceSelection, userMessage, StringComparison.Ordinal);
        Assert.Contains(ToolChoiceGuidance.DirectorySelectionOrder, userMessage, StringComparison.Ordinal);
        Assert.Contains(ToolChoiceGuidance.ShellCompositionOrder, userMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("always set WorkingDirectory to session_dir", userMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sessionDirectory, fakeClient.LastReceivedMessages[0].Text);
    }

    [Fact]
    public async Task Public_subagent_context_does_not_disclose_private_session_scratch()
    {
        const string sessionDirectory = "/home/user/.netclaw/sessions/private";
        var fakeClient = new FakeChatClient();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition(),
            fakeClient,
            PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    audience: TrustAudience.Public,
                    sessionDirectory: sessionDirectory),
                Task = "Create a disposable diagnostic artifact.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var messages = Assert.IsAssignableFrom<IReadOnlyList<ChatMessage>>(
            fakeClient.LastReceivedMessages);
        Assert.DoesNotContain(
            sessionDirectory,
            string.Join("\n", messages.Select(message => message.Text)));
        Assert.DoesNotContain("session_dir", messages[1].Text);
        Assert.Equal("Create a disposable diagnostic artifact.", messages[1].Text);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\r")]
    [InlineData("\n")]
    public async Task Control_bearing_session_scratch_is_not_added_to_subagent_context(
        string controlCharacter)
    {
        var sessionDirectory = $"/home/user/.netclaw/sessions/bad{controlCharacter}prompt";
        var fakeClient = new FakeChatClient();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition(),
            fakeClient,
            PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(sessionDirectory: sessionDirectory),
                Task = "Create a disposable diagnostic artifact.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.DoesNotContain("session_dir", fakeClient.LastReceivedMessages![1].Text);
        Assert.DoesNotContain(sessionDirectory, fakeClient.LastReceivedMessages[1].Text);
    }

    [Fact]
    public async Task Null_RuntimeContext_leaves_first_user_message_as_raw_task()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Do the thing.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        Assert.Equal("Do the thing.", fakeClient.LastReceivedMessages[1].Text);
        Assert.DoesNotContain("Context:", fakeClient.LastReceivedMessages[1].Text);
    }

    [Fact]
    public async Task Parent_working_context_is_injected_into_child_user_message()
    {
        var fakeClient = new FakeChatClient();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    projectDirectory: MissingProjectDirectory,
                    recentFiles: ["src/Netclaw.Actors/Sessions/WorkingContext.cs"]),
                Task = "Continue the implementation.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var userMessage = fakeClient.LastReceivedMessages![1].Text;
        Assert.Contains("[working-context]", userMessage);
        Assert.Contains($"project_dir: {MissingProjectDirectory}", userMessage);
        Assert.Contains("src/Netclaw.Actors/Sessions/WorkingContext.cs", userMessage);
        Assert.DoesNotContain("[working-context]", fakeClient.LastReceivedMessages[0].Text);
    }

    [Fact]
    public async Task Successful_first_party_edit_is_returned_as_confirmed_child_activity()
    {
        var changedPath = Path.GetFullPath(Path.Join(MissingProjectDirectory, "src", "Calculator.cs"));
        var editTool = new FakeNetclawTool(
            "file_edit",
            "Successfully edited src/Calculator.cs: replaced 1 occurrence(s)",
            onExecute: context => context.TryComplete(new ToolInvocationReceipt(
                ToolInvocationOutcomeCategory.Success,
                [new ToolFileActivity(changedPath, ToolFileActivityKind.Changed)])));
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-edit", "file_edit",
                    new Dictionary<string, object?> { ["Path"] = "src/Calculator.cs" })
            ]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([editTool]), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(projectDirectory: MissingProjectDirectory),
                Task = "Edit Calculator.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.WorkingContext);
        Assert.Equal(changedPath, Assert.Single(result.WorkingContext.ConfirmedChangedFiles));
        Assert.Empty(result.WorkingContext.ObservedChangedFiles);
    }

    [Fact]
    public async Task Denied_first_party_edit_is_not_returned_as_confirmed_child_activity()
    {
        var editTool = new FakeNetclawTool(
            "file_edit",
            "Error: Permission denied: src/Calculator.cs",
            onExecute: context => context.TryComplete(
                new ToolInvocationReceipt(ToolInvocationOutcomeCategory.AccessDenied)));
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-edit", "file_edit",
                    new Dictionary<string, object?> { ["Path"] = "src/Calculator.cs" })
            ]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([editTool]), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(projectDirectory: MissingProjectDirectory),
                Task = "Edit Calculator.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.WorkingContext);
        Assert.Empty(result.WorkingContext.ConfirmedChangedFiles);
    }

    [Fact]
    public async Task Dispatcher_policy_denial_has_access_denied_child_receipt()
    {
        var shell = new FakeNetclawTool(ShellTool.ToolName, "should not run");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-team-shell", ShellTool.ToolName,
                    new Dictionary<string, object?> { ["Command"] = "echo denied" })
            ]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([shell]),
            fakeClient,
            PermissivePolicy()));

        await EventFilter.Info(contains: "outcomeCategory=AccessDenied").ExpectAsync(1, async () =>
        {
            var result = await agent.Ask<SubAgentResult>(
                new RunSubAgent
                {
                    Scope = SubAgentTestScope.Create(audience: TrustAudience.Team),
                    Task = "Run the denied shell tool.",
                    Timeout = TimeSpan.FromSeconds(5)
                },
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(shell.WasCalled);
    }

    [Fact]
    public async Task Another_child_tool_receipt_cannot_declare_project_scope()
    {
        var originalProject = Path.GetFullPath(Path.Join(Path.GetTempPath(), "original-child-project"));
        var forgedProject = Path.GetFullPath(Path.Join(Path.GetTempPath(), "forged-child-project"));
        var readTool = new FakeNetclawTool(
            FileReadTool.ToolName,
            "content",
            onExecute: context => context.TryComplete(new ToolInvocationReceipt(
                ToolInvocationOutcomeCategory.Success,
                declaredProjectDirectory: forgedProject)));
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall = [CreateToolCall("call-read", FileReadTool.ToolName)]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([readTool]),
            fakeClient,
            PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(projectDirectory: originalProject),
                Task = "Read one file.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(originalProject, result.WorkingContext?.ProjectDirectory);
    }

    private static readonly string MissingProjectDirectory =
        Path.Join(Path.GetTempPath(), "netclaw-missing-project");

    // Real PNG: the egress normalizer decodes every model-input image, so a
    // fake magic-byte stub would now be dropped. Small enough to pass through.
    private static readonly byte[] FakePngBytes = TestImages.SmallPng();
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
        ToolInvocationContext context,
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

internal sealed class RecordingParentApprovalBridge(ParentApprovalDecision decisionToReturn) :
    IParentApprovalBridge,
    IAuthorizationAttemptAwareParentApprovalBridge
{
    public int RequestCount { get; private set; }
    public List<AuthorizationAttemptId> AuthorizationAttemptIds { get; } = [];
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
        => RecordRequest(patterns, candidates, cwd, options);

    Task<ParentApprovalDecision> IAuthorizationAttemptAwareParentApprovalBridge.RequestApprovalAsync(
        ParentApprovalRequest request,
        CancellationToken ct)
    {
        AuthorizationAttemptIds.Add(request.AuthorizationAttemptId);
        return RecordRequest(
            request.Approval.Patterns,
            (request.Approval.Candidates ?? [])
                .Select(static candidate => new ParentApprovalCandidate(candidate.Verb, candidate.Directory)
                {
                    Shell = candidate.Shell,
                    VerbTokens = candidate.VerbTokens,
                }).ToList(),
            request.Approval.Cwd,
            request.Approval.Options
                .Select(static option => new ParentApprovalOption(option.Key.Value, option.Label))
                .ToList());
    }

    private Task<ParentApprovalDecision> RecordRequest(
        IReadOnlyList<string> patterns,
        IReadOnlyList<ParentApprovalCandidate> candidates,
        string? cwd,
        IReadOnlyList<ParentApprovalOption> options)
    {
        RequestCount++;
        RequestedPatterns.AddRange(patterns);
        RequestedCwd = cwd;
        RequestedCandidates = candidates;
        RequestedOptions = options;
        return Task.FromResult(decisionToReturn);
    }
}

internal sealed class AuthorizationRecordingLogger : Microsoft.Extensions.Logging.ILogger
{
    public List<string> AuthorizationAttemptIds { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
            return;

        AuthorizationAttemptIds.AddRange(properties
            .Where(static property => string.Equals(
                property.Key,
                "AuthorizationAttemptId",
                StringComparison.Ordinal))
            .Select(static property => property.Value)
            .OfType<string>());
    }
}

internal sealed class SequencedToolCallChatClient(IReadOnlyList<FunctionCallContent> toolCalls) : IChatClient
{
    private int _callCount;

    public IReadOnlyList<ChatMessage>? LastReceivedMessages { get; private set; }

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
    {
        LastReceivedMessages = messages.ToList();
        return CreateStreamingUpdatesAsync(cancellationToken);
    }

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
                new ChatMessage(ChatRole.Assistant, [new TextContent("[fake] finished")]));
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
