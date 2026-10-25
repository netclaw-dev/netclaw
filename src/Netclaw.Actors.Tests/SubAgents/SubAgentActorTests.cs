// -----------------------------------------------------------------------
// <copyright file="SubAgentActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading.Channels;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tests.Sessions;
using ApprovalOptionKeys = Netclaw.Actors.Protocol.ApprovalOptionKeys;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tests.Utilities;
using FakeChatClient = Netclaw.Tests.Utilities.FakeChatClient;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

public class SubAgentActorTests : SubAgentActorTestBase
{
    private const string ApprovalProbeToolName = "approval_probe";

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

    public SubAgentActorTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No persistence or hosting needed — SubAgentActor is standalone
    }

    [Fact]
    public async Task Child_does_not_restart_work_while_parent_holds_terminal_commit()
    {
        var client = new FakeChatClient();
        var owner = CreateTestProbe("child-terminal-owner");
        using var execution = new ChildRunExecution(new SubAgentRunId("terminal-ack-run"), owner.Ref);
        var actor = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), client, PermissivePolicy()));
        actor.Tell(new RunSubAgent
        {
            Scope = SubAgentTestScope.Create(),
            Task = "Return a short result.",
            Timeout = TimeSpan.FromSeconds(10),
            OwnedRun = execution,
            Cancellation = execution.Token
        }, owner.Ref);

        var terminal = await owner.ExpectMsgAsync<ChildTerminalReturned>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new SubAgentRunId("terminal-ack-run"), terminal.RunId);
        Assert.Equal(1, client.CallCount);

        var watcher = CreateTestProbe("child-terminal-watcher");
        watcher.Watch(actor);
        owner.Send(actor, new ChildCheckpointCommitted());
        owner.Send(actor, new ChildTerminalCommitted());
        await watcher.ExpectTerminatedAsync(actor, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
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

        // Act
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

    // Production case (child fea1ce26): the child ended with "Now the scene file." and
    // 69042 chars of reasoning, no tool call, finishReason=length. The run reported
    // success=True although the model ran out of output budget before it emitted the
    // tool call. A reply cut off by length without a tool call is never a completion.
    private const string TruncatedStub = "Now the scene file.";

    // Wire value of the new outcome reason. Asserted as a string so the test stays
    // independent of the C# member name.
    private const string OutputTruncatedReason = "output_truncated";

    [Fact]
    public async Task Text_reply_cut_off_by_length_fails_with_output_truncated_reason()
    {
        using var dir = new DisposableTempDir();
        var fakeClient = new FakeChatClient
        {
            ResponseContentsByCall =
            [
                [new TextReasoningContent(new string('t', 20_000)), new TextContent(TruncatedStub)]
            ],
            FinishReasonsByCall = [ChatFinishReason.Length]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(sessionDirectory: dir.Path), Task = "Write the scene file", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(SubAgentRunOutcome.Failed, result.Outcome);
        Assert.Equal(OutputTruncatedReason, result.OutcomeReason?.Value);
        Assert.Contains("truncat", result.Output, StringComparison.OrdinalIgnoreCase);

        // Fail fast: no retry call.
        Assert.Equal(1, fakeClient.CallCount);
    }

    [Fact]
    public async Task Text_reply_that_ends_with_stop_still_completes()
    {
        var fakeClient = new FakeChatClient
        {
            ResponseContentsByCall =
            [
                [new TextReasoningContent("plan the answer"), new TextContent("The scene file is ready.")]
            ],
            FinishReasonsByCall = [ChatFinishReason.Stop]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Write the scene file", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Completed, result.Outcome);
        Assert.Null(result.OutcomeReason);
        Assert.Equal("The scene file is ready.", result.Output);
    }

    [Fact]
    public async Task Tool_call_response_that_ends_with_length_still_runs_the_tool()
    {
        // Today a tool call wins over the finish reason. Keep it: only a reply with
        // no tool call is a failure.
        var fakeTool = new FakeNetclawTool("greet", "Hello from tool!");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-1", "greet", new Dictionary<string, object?> { ["name"] = "World" })
            ],
            FinishReasonsByCall = [ChatFinishReason.Length]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition([fakeTool]), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Greet the user", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(fakeTool.WasCalled);
        Assert.True(result.Success);
        Assert.Contains("Response #2", result.Output);
    }

    [Fact]
    public async Task Thinking_only_reply_cut_off_by_length_still_retries_and_recovers()
    {
        // The existing ThinkingOnly retry path must keep working next to the new
        // text-reply rule.
        var fakeClient = new FakeChatClient
        {
            ResponseContentsByCall = [[new TextReasoningContent("still thinking")]],
            FinishReasonsByCall = [ChatFinishReason.Length, ChatFinishReason.Stop],
            ResponseText = "Recovered answer."
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(CreateDefinition(), fakeClient, PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Answer please", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("Recovered answer.", result.Output);
        Assert.Equal(2, fakeClient.CallCount);
    }

    [Fact]
    public async Task Tool_results_attach_two_ordered_media_files_in_one_follow_up_nudge()
    {
        // Arrange
        using var dir = new DisposableTempDir();
        var firstImagePath = Path.Combine(dir.Path, "first.png");
        var secondImagePath = Path.Combine(dir.Path, "second.png");
        var firstImage = TestImages.SmallPng();
        var secondImage = TestImages.SmallPng(17);
        await File.WriteAllBytesAsync(firstImagePath, firstImage, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(secondImagePath, secondImage, TestContext.Current.CancellationToken);
        var firstTool = new FakeNetclawTool(
            "load_first_image",
            "first image loaded",
            onExecute: context => context.AddModelInputFile(firstImagePath, "first.png", "image/png"));
        var secondTool = new FakeNetclawTool(
            "load_second_image",
            "second image loaded",
            onExecute: context => context.AddModelInputFile(secondImagePath, "second.png", "image/png"));
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-first-image", "load_first_image"),
                CreateToolCall("call-second-image", "load_second_image")
            ]
        };
        var definition = CreateDefinition([firstTool, secondTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, PermissivePolicy()));

        // Act
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

        var followUpMessages = fakeClient.ReceivedMessagesByCall[1];
        var toolResults = followUpMessages
            .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .ToArray();
        var nudges = followUpMessages.Where(message =>
            message.Role == ChatRole.User
            && message.Text.Contains("2 media files", StringComparison.Ordinal)).ToArray();
        var nudge = nudges.FirstOrDefault();
        var nudgeIndex = nudge is null ? -1 : followUpMessages.Select((message, index) => (message, index))
            .First(item => ReferenceEquals(item.message, nudge)).index;
        var toolResultIndexes = followUpMessages.Select((message, index) => (message, index))
            .Where(item => item.message.Contents.OfType<FunctionResultContent>().Any())
            .Select(item => item.index)
            .ToArray();
        var media = nudge?.Contents.OfType<DataContent>().ToArray() ?? [];

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(firstTool.LastContext);
        Assert.True(firstTool.LastContext!.ModelInputModalities.HasFlag(ModelModality.Image));
        Assert.Equal(["call-first-image", "call-second-image"], toolResults.Select(static item => item.CallId));
        Assert.Single(nudges);
        Assert.True(nudgeIndex > toolResultIndexes.DefaultIfEmpty(-1).Max());
        Assert.Equal(2, media.Length);
        Assert.All(media, data => Assert.Equal("image/png", data.MediaType));
        Assert.Equal(firstImage, media[0].Data.ToArray());
        Assert.Equal(secondImage, media[1].Data.ToArray());
    }

    [Fact]
    public async Task Tool_execution_inherits_parent_session_and_project_directories()
    {
        var sessionDirectory = TestPath("sessions", "abc");
        var projectDirectory = TestPath("projects", "netclaw");
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
                    sessionDirectory: sessionDirectory,
                    projectDirectory: projectDirectory,
                    recentFiles: ["src/Netclaw.Actors/SubAgents/SubAgentActor.cs"]),
                Task = "Inspect the inherited paths.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.Equal(sessionDirectory, fakeTool.LastContext!.SessionDirectory);
        Assert.Equal(projectDirectory, fakeTool.LastContext.ProjectDirectory);
        Assert.Equal(["src/Netclaw.Actors/SubAgents/SubAgentActor.cs"], fakeTool.LastContext.RecentFiles);
    }

    [Fact]
    public async Task Tool_execution_with_no_parent_project_directory_passes_null_through()
    {
        var sessionDirectory = TestPath("sessions", "xyz");
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
                Scope = SubAgentTestScope.Create(sessionDirectory: sessionDirectory),
                Task = "Inspect inherited paths.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.Equal(sessionDirectory, fakeTool.LastContext!.SessionDirectory);
        Assert.Null(fakeTool.LastContext.ProjectDirectory);
    }

    [Fact]
    public async Task Tool_execution_inherits_parent_resolved_cwd_snapshot()
    {
        var sessionDirectory = TestPath("sessions", "parent");
        var projectDirectory = TestPath("projects", "foo");
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
                    sessionDirectory: sessionDirectory,
                    projectDirectory: projectDirectory,
                    inheritedCwd: projectDirectory),
                Task = "Inspect inherited cwd.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeTool.LastContext);
        Assert.Equal(projectDirectory, fakeTool.LastContext!.InheritedCwd);
        // ProjectDirectory wins the resolve when set; this asserts that the
        // inherited snapshot doesn't shadow it.
        Assert.Equal(projectDirectory, fakeTool.LastContext.ResolveShellCwd(null));
    }

    [Fact]
    public async Task Tool_execution_with_null_parent_cwd_resolves_to_child_session_directory()
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
        Assert.Equal(fakeTool.LastContext.SessionDirectory, fakeTool.LastContext.ResolveShellCwd(null));
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
    public async Task Session_storage_context_does_not_authorize_headless_prompt_worthy_shell()
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

        // D2: a child with no approval bridge cannot ask, so the call that needs
        // approval is denied and never runs. The child gets the denial as a tool result.
        Assert.False(fakeTool.WasCalled);
        Assert.Contains("nobody can answer a prompt", GetLastToolResult(fakeClient, "call-approval"), StringComparison.Ordinal);
        var context = fakeClient.LastReceivedMessages![1].Text;
        Assert.Contains($"session_dir: {sessionDirectory}", context);
        Assert.Contains($"worktree_dir: {Path.Combine(sessionDirectory, "worktrees")}", context);
    }

    [Fact]
    public async Task Subagent_approval_request_carries_cwd_candidates_and_full_button_set()
    {
        // Regression: the parent approval bridge previously hardcoded a
        // 4-button list and dropped Cwd/Candidates entirely, so sub-agent
        // approval prompts showed "(no working directory)" and were missing
        // the Always-anywhere button regardless of what the sub-agent's
        // resolved cwd was.
        var fakeTool = new FakeNetclawTool(ShellTool.ToolName, "should not run");
        var policy = CreateApprovalRequiredPolicy();

        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-cwd-prompt", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        var sessionDirectory = TestPath("sessions", "approval-parent");
        var projectDirectory = TestPath("projects", "approval-project");
        var logger = new AuthorizationRecordingLogger();
        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreatePropsWithProjectInstructionProvider(
            definition,
            fakeClient,
            policy,
            NullSystemPromptProvider.Instance,
            toolExecutorLogger: logger));

        // The child parks the call (durable-child-consent-wait, design D1). The park request carries the gate facts.
        var (_, park, requests) = await RunToParkAsync(agent, SubAgentTestScope.Create(
            sessionDirectory: sessionDirectory,
            projectDirectory: projectDirectory,
            inheritedCwd: projectDirectory,
            approvalBridge: new ConsentCapableBridge()), "Push to origin");

        Assert.False(fakeTool.WasCalled);
        var request = Assert.Single(requests);
        Assert.True(AuthorizationAttemptId.TryParse(request.AuthorizationAttemptId.Value, out _));
        Assert.NotEmpty(logger.AuthorizationAttemptIds);
        Assert.All(
            logger.AuthorizationAttemptIds,
            loggedAttemptId => Assert.Equal(request.AuthorizationAttemptId.Value, loggedAttemptId));
        Assert.Equal(projectDirectory, request.Approval.Cwd);
        var candidate = Assert.Single(request.Approval.Candidates ?? []);
        Assert.Equal("git push origin main", candidate.Verb);
        var options = request.Approval.Options.Select(static option => option.Key.Value).ToArray();
        Assert.Contains(ApprovalOptionKeys.ApproveEverywhere, options);
        Assert.Contains(ApprovalOptionKeys.ApproveAlways, options);
        Assert.Contains(ApprovalOptionKeys.ApproveSession, options);
    }

    [Fact]
    public async Task Subagent_platform_temp_call_receives_managed_temporary_correction_before_parent_bridge()
    {
        var sessionDirectory = TestPath("sessions", "managed-temporary-example");
        var fakeTool = new FakeNetclawTool(ShellTool.ToolName, "should not run");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                PlatformTemporaryCall("call-managed-temporary-correction")
            ]
        };
        var approvalBridge = new ConsentCapableBridge();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([fakeTool]),
            fakeClient,
            CreateManagedTemporaryCorrectionPolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: sessionDirectory,
                    approvalBridge: approvalBridge),
                Task = "Inspect a disposable diagnostic artifact.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Output);
        Assert.False(fakeTool.WasCalled);
        Assert.Equal(
            "Tool execution deferred: use_managed_temporary_directory\n" +
            $"Managed temporary directory: '{Path.Combine(sessionDirectory, "subagents", "run", "tmp")}'.\n" +
            "Next action: use the managed temporary directory from this result for disposable files, or retry unchanged for exact platform paths.",
            GetLastToolResult(fakeClient, "call-managed-temporary-correction"));
    }

    [Fact]
    public async Task Subagent_delivers_native_and_temporary_corrections_without_execution_or_approval()
    {
        var sessionDirectory = TestPath("sessions", "native-temporary-correction");
        var environment = TestShellEnvironment.Current;
        var shell = new FakeNetclawTool(ShellTool.ToolName, "should not run");
        var fileWrite = new FileWriteTool(
            new ToolConfig(),
            new NetclawPaths(),
            new ToolPathPolicy(environment, []));
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                NativeTemporaryFileWriteCall("call-native-temporary-correction")
            ]
        };
        var approvalBridge = new ConsentCapableBridge();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([shell, fileWrite]),
            fakeClient,
            CreateManagedTemporaryCorrectionPolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: sessionDirectory,
                    approvalBridge: approvalBridge),
                Task = "Write a disposable diagnostic artifact.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Output);
        Assert.False(shell.WasCalled);
        Assert.Equal(
            "Shell execution stopped because 'file_write' is a native Netclaw tool.\n" +
            $"Managed temporary directory: '{Path.Combine(sessionDirectory, "subagents", "run", "tmp")}'.\n" +
            "Next action: call the native Netclaw tool named in this result directly instead of shell_execute.",
            GetLastToolResult(fakeClient, "call-native-temporary-correction"));
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
            new NetclawPaths(worktree, worktree),
            new ToolPathPolicy([]));
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
        var sessionDirectory = TestPath("sessions", "managed-temporary-parallel");
        var fakeTool = new FakeNetclawTool(ShellTool.ToolName, "should not run");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                PlatformTemporaryCall("call-managed-temporary-parallel-1"),
                PlatformTemporaryCall("call-managed-temporary-parallel-2")
            ]
        };
        var approvalBridge = new ConsentCapableBridge();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([fakeTool]),
            fakeClient,
            CreateManagedTemporaryCorrectionPolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    sessionDirectory: sessionDirectory,
                    approvalBridge: approvalBridge),
                Task = "Inspect two disposable diagnostic artifacts.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Output);
        Assert.False(fakeTool.WasCalled);
        Assert.Contains(
            "use_managed_temporary_directory",
            GetLastToolResult(fakeClient, "call-managed-temporary-parallel-1"));
        Assert.Contains(
            "use_managed_temporary_directory",
            GetLastToolResult(fakeClient, "call-managed-temporary-parallel-2"));
    }

    [Fact]
    public async Task Subagent_exact_temp_retry_parks_with_once_or_deny()
    {
        var sessionDirectory = TestPath("sessions", "managed-temporary-retry");
        var fakeTool = new FakeNetclawTool(ShellTool.ToolName, "should not run");
        var fakeClient = new SequencedToolCallChatClient(
        [
            PlatformTemporaryCall("call-managed-temporary-first"),
            PlatformTemporaryCall("call-managed-temporary-retry")
        ]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([fakeTool]),
            fakeClient,
            CreateManagedTemporaryCorrectionPolicy()));

        // The retry target of a parked managed temporary retry is deferred to a later PR (cut plan), so this test
        // checks only the options of the parked request.
        var (_, park, requests) = await RunToParkAsync(agent, SubAgentTestScope.Create(
            sessionDirectory: sessionDirectory,
            approvalBridge: new ConsentCapableBridge()), "Retry the exact disposable diagnostic call if corrected.");

        Assert.False(fakeTool.WasCalled);
        var request = Assert.Single(requests);
        Assert.Equal("call-managed-temporary-retry", request.CallId.Value);
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            request.Approval.Options.Select(static option => option.Key.Value));
    }

    internal static ToolAccessPolicy PermissivePolicy() => new(
        new NetclawPaths(),
        new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
        new EffectivePolicyDefaults(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false),
        new ShellCommandPolicy(),
        new ToolPathPolicy([]));

    private static ToolAccessPolicy CreateApprovalRequiredPolicy(string? netclawHome = null)
        => CreateApprovalRequiredPolicy(ShellTool.ToolName, netclawHome);

    private static ToolAccessPolicy CreateApprovalRequiredPolicy(
        string toolName,
        string? netclawHome)
    {
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [toolName] = ToolApprovalMode.Approval
            }
        };
        var environment = TestShellEnvironment.Current;
        return new ToolAccessPolicy(
            netclawHome is null ? new NetclawPaths() : new NetclawPaths(netclawHome),
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(environment),
            new ToolPathPolicy(environment, []));
    }

    private static ToolAccessPolicy CreateManagedTemporaryCorrectionPolicy()
    {
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [ShellTool.ToolName] = ToolApprovalMode.Approval
            }
        };
        var environment = TestShellEnvironment.Current;
        var commandPolicy = new ShellCommandPolicy(environment);
        var pathPolicy = new ToolPathPolicy(environment, []);
        return new ToolAccessPolicy(
            new NetclawPaths(),
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            pathPolicy,
            new TemporaryPathCorrectionPolicy(
                environment,
                Path.GetFullPath(Path.GetTempPath()),
                new AlwaysSafeTemporaryPathInspector()));
    }

    private static FunctionCallContent PlatformTemporaryCall(string callId)
        => new(callId, ShellTool.ToolName, new Dictionary<string, object?>
        {
            ["Command"] = TestShellEnvironment.Current.Grammar == ShellGrammar.Bash
                ? "gh api repos/example/project"
                : "Get-Content result.log",
            ["WorkingDirectory"] = Path.GetFullPath(Path.GetTempPath()),
            ["_rationale"] = "Inspect a disposable diagnostic artifact."
        });

    private static FunctionCallContent NativeTemporaryFileWriteCall(string callId)
        => new(callId, ShellTool.ToolName, new Dictionary<string, object?>
        {
            ["Command"] = TestShellEnvironment.Current.Grammar == ShellGrammar.Bash
                ? $"file_write --path {Path.Combine(Path.GetTempPath(), "subagent-output.txt")}"
                : $"file_write -Path {Path.Combine(Path.GetTempPath(), "subagent-output.txt")}",
            ["WorkingDirectory"] = Path.GetFullPath(Path.GetTempPath()),
            ["_rationale"] = "Write a disposable diagnostic artifact."
        });

    private sealed class AlwaysSafeTemporaryPathInspector : IPlatformTemporaryPathInspector
    {
        public bool TryResolveRoot(
            string path,
            ShellPathStyle pathStyle,
            out string resolvedRoot)
        {
            var created = CanonicalPath.TryCreate(path, relativeBase: null, pathStyle, out var root);
            resolvedRoot = created ? root.Value : string.Empty;
            return created;
        }

        public bool HasNoLinkEscape(string root, string path, ShellPathStyle pathStyle)
            => true;

        public bool SupportsPathInspection(ShellPathStyle pathStyle)
            => true;
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

    // An owned run whose owner is a probe. The probe acknowledges each checkpoint, as the session does, and the
    // method returns the park of the child and its requests (durable-child-consent-wait, design D1).
    private async Task<ChildParkReached> RunToParkAsync(IActorRef agent, ChildRunScope scope, string task)
    {
        var owner = CreateTestProbe();
        var execution = new ChildRunExecution(new SubAgentRunId("run"), owner.Ref);
        agent.Tell(new RunSubAgent
        {
            Scope = scope, Task = task, Timeout = TimeSpan.FromSeconds(5), OwnedRun = execution,
            Cancellation = execution.Token
        }, owner.Ref);
        while (true)
        {
            var message = await owner.ExpectMsgAsync<object>(ApprovalAskTimeout, cancellationToken: TestContext.Current.CancellationToken);
            if (message is ChildParkReached reached)
                return reached;
            Assert.IsType<ChildCheckpointReached>(message);
            owner.Reply(SessionProtocol.CommandAck.For(new Netclaw.Actors.Protocol.SessionId("test-session")));
        }
    }

    [Fact]
    public async Task Distinct_productive_tool_rounds_continue_past_the_former_limit()
    {
        const int FormerDefaultLimit = 30;
        const int UsefulRounds = FormerDefaultLimit + 1;
        var tool = new ProgressTool();
        var responses = Enumerable.Range(1, UsefulRounds)
            .Select(round => (IReadOnlyList<AIContent>)[CreateToolCall(
                $"call-{round}",
                "looper",
                new Dictionary<string, object?> { ["query"] = $"step-{round}" })])
            .ToArray();
        var fakeClient = new ScriptedToolCallChatClient(responses, "Task complete.");
        var definition = CreateDefinition([tool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            definition,
            fakeClient,
            PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Scope = SubAgentTestScope.Create(), Task = "Use each distinct result", Timeout = TimeSpan.FromSeconds(30) },
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Completed, result.Outcome);
        Assert.Null(result.OutcomeReason);
        Assert.Equal("Task complete.", result.Output);
        Assert.Equal(UsefulRounds + 1, fakeClient.CallCount);
        Assert.Equal(Enumerable.Range(1, UsefulRounds).Select(static round => $"step-{round}"), tool.Queries);
    }

    [Fact]
    public async Task Repeated_corrected_call_is_refused_again_with_a_nudge_and_the_child_continues()
    {
        var diagnostics = CreateTestProbe();
        Sys.EventStream.Subscribe(diagnostics, typeof(Warning));
        var tool = new ProgressTool();
        FunctionCallContent Call(string callId, string query)
            => CreateToolCall(callId, "looper", new Dictionary<string, object?> { ["query"] = query });
        IReadOnlyList<AIContent>[] responses =
        [
            [Call("repeat-1", "repeat"), Call("diagnostic-1", "diagnostic-1")],
            [Call("repeat-2", "repeat"), Call("diagnostic-2", "diagnostic-2")],
            [Call("repeat-3", "repeat"), Call("diagnostic-3", "diagnostic-3")],
            [Call("repeat-4", "repeat"), Call("diagnostic-4", "diagnostic-4")]
        ];
        var fakeClient = new ScriptedToolCallChatClient(responses, "The repeat stalls. Diagnostics 1 to 4 ran.");

        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([tool]),
            fakeClient,
            PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Repeat one call and run a diagnostic each round.",
                Timeout = TimeSpan.FromSeconds(10)
            },
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        // The child is not stopped. It reads the nudge and completes with its own answer.
        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Completed, result.Outcome);
        Assert.Null(result.OutcomeReason);
        Assert.Equal("The repeat stalls. Diagnostics 1 to 4 ran.", result.Output);
        Assert.Equal(5, fakeClient.CallCount);
        // The new diagnostic next to the repeat still runs. Neither refused repeat runs.
        Assert.Equal(
            ["diagnostic-1", "diagnostic-2", "diagnostic-3", "diagnostic-4", "repeat", "repeat"],
            tool.Queries.Order(StringComparer.Ordinal));
        var lastRequest = fakeClient.ReceivedMessagesByCall[^1];
        var toolResults = lastRequest
            .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Select(static toolResult => toolResult.Result?.ToString() ?? string.Empty)
            .ToList();
        Assert.Equal(2, toolResults.Count(static text => text == "result:repeat"));
        Assert.Equal(4, toolResults.Count(static text => text.StartsWith("result:diagnostic-", StringComparison.Ordinal)));
        Assert.Equal(2, toolResults.Count(static text =>
            text.Contains("same action completed twice", StringComparison.Ordinal)));

        // The first correction has no nudge. The repeat gets the child nudge as the last message.
        static bool IsChildNudge(ChatMessage message)
            => message.Role == Microsoft.Extensions.AI.ChatRole.User
                && message.Text.StartsWith("[system: Netclaw refused your repeated call to `looper` again.",
                    StringComparison.Ordinal);
        Assert.DoesNotContain(fakeClient.ReceivedMessagesByCall[3], IsChildNudge);
        var nudge = lastRequest[^1];
        Assert.True(IsChildNudge(nudge), $"The last message was: {nudge.Text}");
        // A child has no background job manager, so its nudge sends the long command to the parent.
        Assert.DoesNotContain("_background", nudge.Text, StringComparison.Ordinal);
        Assert.Contains("parent", nudge.Text, StringComparison.Ordinal);
        Assert.Contains("did not execute", nudge.Text, StringComparison.Ordinal);

        for (var i = 0; i < 2; i++)
        {
            var diagnostic = await diagnostics.FishForMessageAsync<Warning>(
                warning => warning.Message.ToString()!.StartsWith("Subagent tool cycle decision", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(typeof(Netclaw.Actors.Sessions.Handlers.TurnStateTracker), diagnostic.LogClass);
            Assert.Equal($"TurnStateTracker (akka://{Sys.Name})", diagnostic.LogSource);
            Assert.DoesNotContain(agent.Path.Name, diagnostic.LogSource, StringComparison.Ordinal);
            var properties = Assert.IsAssignableFrom<LogMessage>(diagnostic.Message).GetProperties();
            Assert.Equal(["DecisionKind", "RefusedCalls", "RepeatedCalls"], properties.Keys.Order(StringComparer.Ordinal));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_ends_quietly_with_its_last_text_after_it_ignores_the_escalated_nudge(bool finalResponseHasText)
    {
        // Aaron's rule (18 Oct 2026), child form. Rounds 1 and 2 run, round 3 is the first correction,
        // rounds 4 to 7 are refused repeats 1 to 4, round 8 is repeat 5 (the escalation), and round 9 is
        // repeat 6. Repeat 6 ends the child with the last text that the child wrote. The parent sees no error.
        const string EarlierText = "The looper call returns the same value. I try once more.";
        const string FinalText = "The looper call stays blocked. The repeat check did not finish.";
        var tool = new ProgressTool();
        FunctionCallContent Call(int round)
            => CreateToolCall($"repeat-{round}", "looper", new Dictionary<string, object?> { ["query"] = "repeat" });
        var responses = new List<IReadOnlyList<AIContent>>();
        for (var round = 1; round <= 8; round++)
        {
            responses.Add(round == 4
                ? [new TextContent(EarlierText), Call(round)]
                : [Call(round)]);
        }
        responses.Add(finalResponseHasText ? [new TextContent(FinalText), Call(9)] : [Call(9)]);
        var fakeClient = new ScriptedToolCallChatClient(responses, "This response must not run.");

        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([tool]),
            fakeClient,
            PermissivePolicy()));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Repeat one call.",
                Timeout = TimeSpan.FromSeconds(10)
            },
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        // Repeats 1 to 4 get the plain child nudge. Repeat 5 gets the escalated child nudge.
        static bool IsChildNudge(ChatMessage message)
            => message.Role == Microsoft.Extensions.AI.ChatRole.User
                && message.Text.StartsWith("[system: Netclaw refused your repeated call to `looper` again.",
                    StringComparison.Ordinal);
        const string EscalationMarker = "Stop trying this action.";
        var requests = fakeClient.ReceivedMessagesByCall;
        for (var request = 4; request <= 7; request++)
        {
            Assert.True(IsChildNudge(requests[request][^1]), $"Request {request} must end with the child nudge.");
            Assert.DoesNotContain(EscalationMarker, requests[request][^1].Text, StringComparison.Ordinal);
        }

        var escalation = requests[8][^1];
        Assert.True(IsChildNudge(escalation), $"The last message was: {escalation.Text}");
        Assert.Contains(EscalationMarker, escalation.Text, StringComparison.Ordinal);
        Assert.Contains("parent", escalation.Text, StringComparison.Ordinal);
        Assert.Contains("No call in that response runs.", escalation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("_background", escalation.Text, StringComparison.Ordinal);
        // Only the latest child nudge stays in the child history.
        for (var request = 4; request <= 8; request++)
            Assert.Single(requests[request], IsChildNudge);

        // Repeat 6 ends the child with no further model request and no execution.
        Assert.Equal(9, fakeClient.CallCount);
        Assert.Equal(["repeat", "repeat"], tool.Queries);
        // A successful partial run: the parent gets the child text, not an error.
        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Partial, result.Outcome);
        Assert.Equal(SubAgentOutcomeReason.ToolCycleStopped, result.OutcomeReason);
        Assert.Equal(finalResponseHasText ? FinalText : EarlierText, result.Output);
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
        var policy = new ToolAccessPolicy(new NetclawPaths(),
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
    public async Task Eligible_subagent_context_announces_exact_private_storage_paths(
        TrustAudience audience)
    {
        var sessionDirectory = TestPath("sessions", "context-example");
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
        Assert.Contains($"temp_dir: {Path.Combine(sessionDirectory, "subagents", "run", "tmp")}", userMessage);
        Assert.Contains($"artifact_dir: {Path.Combine(sessionDirectory, "subagents", "run", "artifacts")}", userMessage);
        Assert.Contains($"worktree_dir: {Path.Combine(sessionDirectory, "worktrees")}", userMessage);
        Assert.Contains(ToolChoiceGuidance.StructuredWorkspaceSelection, userMessage, StringComparison.Ordinal);
        Assert.Contains(ToolChoiceGuidance.DirectorySelectionOrder, userMessage, StringComparison.Ordinal);
        Assert.Contains(ToolChoiceGuidance.ShellCompositionOrder, userMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("always set WorkingDirectory to session_dir", userMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sessionDirectory, fakeClient.LastReceivedMessages[0].Text);
    }

    [Fact]
    public async Task Public_subagent_context_does_not_disclose_private_storage_paths()
    {
        var sessionDirectory = TestPath("sessions", "private");
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
    public void Control_bearing_session_directory_is_rejected_before_subagent_context(
        string controlCharacter)
    {
        var sessionDirectory = $"/home/user/.netclaw/sessions/bad{controlCharacter}prompt";
        Assert.Throws<ArgumentException>(() =>
            SubAgentTestScope.Create(sessionDirectory: sessionDirectory));
    }

    [Fact]
    public async Task Null_runtime_context_still_includes_session_storage_context()
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
        var userText = fakeClient.LastReceivedMessages[1].Text;
        Assert.Contains("Context:", userText);
        Assert.Contains("[session]", userText);
        Assert.Contains("Task:\nDo the thing.", userText);
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
            onExecute: context => context.TryComplete(new ToolInvocationReceipt.Succeeded([new ToolFileActivity(changedPath, ToolFileActivityKind.Changed)], null)));
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
                new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.AccessDenied)));
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
    public async Task Parallel_tool_results_keep_input_order_after_reverse_completion()
    {
        // Arrange
        var changedPath = Path.GetFullPath(Path.Join(MissingProjectDirectory, "src", "Parallel.cs"));
        var fakeTool = new ReverseCompletionTool(changedPath);
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("call-first", "controlled_tool", new Dictionary<string, object?> { ["label"] = "first" }),
                CreateToolCall("call-second", "controlled_tool", new Dictionary<string, object?> { ["label"] = "second" })
            ]
        };
        var diagnostics = CreateTestProbe();
        Sys.EventStream.Subscribe(diagnostics, typeof(Info));
        var agent = Sys.ActorOf(SubAgentActor.CreatePropsWithProjectInstructionProvider(
            CreateDefinition([fakeTool]),
            fakeClient,
            PermissivePolicy(),
            NullSystemPromptProvider.Instance));

        // Act
        var runTask = agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(projectDirectory: MissingProjectDirectory),
                Task = "Run two related operations.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await fakeTool.BothCallsStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fakeTool.FailSecondCall();
        var secondFailure = await Record.ExceptionAsync(async () => await fakeTool.SecondExecution);
        fakeTool.CompleteFirstCall();

        var result = await runTask;
        var confirmedFiles = result.WorkingContext?.ConfirmedChangedFiles;

        var followUpMessages = fakeClient.ReceivedMessagesByCall[1];
        var toolResults = followUpMessages
            .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .ToArray();

        var authorizationStartLogs = new List<object>();
        for (var i = 0; i < 2; i++)
        {
            var log = await diagnostics.FishForMessageAsync<Info>(
                message => message.Message.ToString()!.StartsWith(
                    "SubAgent [test-agent] authorization attempt started",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
            authorizationStartLogs.Add(log.Message);
        }

        var authorizationResultLogs = new List<object>();
        for (var i = 0; i < 2; i++)
        {
            var log = await diagnostics.FishForMessageAsync<Info>(
                message => message.Message.ToString()!.StartsWith(
                    "SubAgent authorization attempt result",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
            authorizationResultLogs.Add(log.Message);
        }

        var authorizationStarts = authorizationStartLogs
            .OfType<LogMessage>()
            .Select(static message => message.GetProperties())
            .ToArray();
        var authorizationResults = authorizationResultLogs
            .OfType<LogMessage>()
            .Select(static message => message.GetProperties())
            .ToArray();

        // Assert
        Assert.IsType<UnauthorizedAccessException>(secondFailure);
        Assert.True(result.Success, result.Output);
        Assert.Equal([changedPath], confirmedFiles);
        Assert.Equal(["call-first", "call-second"], toolResults.Select(static item => item.CallId));
        Assert.Equal("first result", toolResults[0].Result?.ToString());
        Assert.Contains("controlled denial", toolResults[1].Result?.ToString(), StringComparison.Ordinal);
        var startAttemptIdsByCallId = authorizationStarts.ToDictionary(
            static properties => properties["CallId"].ToString()!,
            static properties => properties["AuthorizationAttemptId"].ToString()!,
            StringComparer.Ordinal);
        Assert.Equal(2, startAttemptIdsByCallId.Count);
        Assert.Equal(["call-first", "call-second"], authorizationResults.Select(static properties => properties["CallId"].ToString()));
        Assert.Equal(["Success", "AccessDenied"], authorizationResults.Select(static properties => properties["OutcomeCategory"]?.ToString()));
        Assert.All(authorizationResults, properties =>
            Assert.Equal(
                startAttemptIdsByCallId[properties["CallId"].ToString()!],
                properties["AuthorizationAttemptId"].ToString()));
    }

    [Fact]
    public async Task Duplicate_normal_tool_call_ids_fail_after_execution_without_follow_up()
    {
        // Arrange
        var executionCount = 0;
        var fakeTool = new FakeNetclawTool(
            "duplicate_tool",
            "ran",
            onExecute: _ => Interlocked.Increment(ref executionCount));
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                CreateToolCall("duplicate-id", "duplicate_tool"),
                CreateToolCall("duplicate-id", "duplicate_tool")
            ]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([fakeTool]),
            fakeClient,
            PermissivePolicy()));

        // Act
        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(),
                Task = "Return two tool results.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var acceptedResults = fakeClient.ReceivedMessagesByCall[0]
            .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .ToArray();

        // Assert
        Assert.False(result.Success);
        Assert.Equal(SubAgentOutcomeReason.ToolExecutionFailed, result.OutcomeReason);
        Assert.Equal(2, executionCount);
        Assert.Equal(1, fakeClient.CallCount);
        Assert.Empty(acceptedResults);
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
        await using var originalProjectDir = TestSessionTempDirectory.Create("netclaw-original-child-");
        await using var forgedProjectDir = TestSessionTempDirectory.Create("netclaw-forged-child-");
        var originalProject = originalProjectDir.Path;
        var forgedProject = forgedProjectDir.Path;
        var readTool = new FakeNetclawTool(
            FileReadTool.ToolName,
            "content",
            onExecute: context => context.TryComplete(new ToolInvocationReceipt.Succeeded([], forgedProject)));
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

internal sealed class ProgressTool : INetclawTool
{
    private readonly ConcurrentQueue<string> _queries = new();

    public string Name => "looper";
    public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
    public string Description => "Returns a result for one distinct query.";
    public string GrantCategory => "builtin";
    public System.Text.Json.JsonElement ParameterSchema => default;
    public IReadOnlyList<string> Queries => _queries.ToArray();

    public AITool ToAITool() => AIFunctionFactory.Create(
        (string query) => $"result:{query}",
        name: Name,
        description: Description);

    public Task<string> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var query = arguments?["query"]?.ToString()
            ?? throw new InvalidOperationException("The query argument is required.");
        _queries.Enqueue(query);
        return Task.FromResult($"result:{query}");
    }
}

internal sealed class ScriptedToolCallChatClient(
    IReadOnlyList<IReadOnlyList<AIContent>> toolResponses,
    string finalResponse) : IChatClient
{
    private readonly List<IReadOnlyList<ChatMessage>> _receivedMessages = [];
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);
    public IReadOnlyList<IReadOnlyList<ChatMessage>> ReceivedMessagesByCall => _receivedMessages;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var callNumber = Record(messages);
        return Task.FromResult(Response(callNumber));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var callNumber = Record(messages);
        return StreamResponse(Response(callNumber), cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    private int Record(IEnumerable<ChatMessage> messages)
    {
        var callNumber = Interlocked.Increment(ref _callCount);
        _receivedMessages.Add(messages.ToArray());
        return callNumber;
    }

    private ChatResponse Response(int callNumber)
    {
        IReadOnlyList<AIContent> contents = callNumber <= toolResponses.Count
            ? toolResponses[callNumber - 1]
            : [new TextContent(finalResponse)];
        return new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, contents.ToList()));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamResponse(
        ChatResponse response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }
}

internal sealed class ReverseCompletionTool(string changedPath) : INetclawTool
{
    private readonly TaskCompletionSource<string> _firstResult =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _secondResult =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _bothCallsStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<string>? _secondExecution;
    private int _startedCalls;

    public string Name => "controlled_tool";
    public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
    public string Description => "Controlled tool for child result order tests.";
    public string GrantCategory => "builtin";
    public System.Text.Json.JsonElement ParameterSchema => default;
    public Task BothCallsStarted => _bothCallsStarted.Task;
    public Task<string> SecondExecution => _secondExecution
        ?? throw new InvalidOperationException("The second tool call has not started.");

    public AITool ToAITool() => AIFunctionFactory.Create(
        () => "controlled",
        name: Name,
        description: Description);

    public Task<string> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        CancellationToken ct = default)
    {
        var label = arguments?["label"] as string
            ?? throw new InvalidOperationException("The controlled tool needs a label.");
        var execution = label switch
        {
            "first" => StartFirst(context).WaitAsync(ct),
            "second" => _secondExecution = _secondResult.Task.WaitAsync(ct),
            _ => throw new InvalidOperationException($"Unknown controlled label: {label}")
        };
        if (Interlocked.Increment(ref _startedCalls) == 2)
            _bothCallsStarted.TrySetResult();

        return execution;
    }

    public void CompleteFirstCall()
        => _firstResult.TrySetResult("first result");

    public void FailSecondCall()
        => _secondResult.TrySetException(new UnauthorizedAccessException("controlled denial"));

    private Task<string> StartFirst(ToolInvocationContext context)
    {
        context.TryComplete(new ToolInvocationReceipt.Succeeded(
            [new ToolFileActivity(changedPath, ToolFileActivityKind.Changed)],
            null));
        return _firstResult.Task;
    }
}

/// <summary>
/// A capability marker: an interactive requester can answer a consent request. An owned child parks at a consent need
/// (durable-child-consent-wait, design D1). It never calls the bridge.
/// </summary>
internal sealed class ConsentCapableBridge : IParentApprovalBridge;

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
