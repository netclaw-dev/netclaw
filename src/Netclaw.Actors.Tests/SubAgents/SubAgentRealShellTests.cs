// -----------------------------------------------------------------------
// <copyright file="SubAgentRealShellTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
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

/// <summary>
/// Sub-agent tests that start a real shell process. The tests check path scope and
/// approval behavior, not the no-activity watchdog. The actor system runs on
/// <see cref="Akka.TestKit.TestScheduler"/>, so virtual time does not move and the
/// watchdog cannot fire while a cold shell starts on a slow runner. A test in this
/// class that needs the watchdog to fire must advance the scheduler.
/// </summary>
public class SubAgentRealShellTests : SubAgentActorTestBase
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    public SubAgentRealShellTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.AddHocon(
            "akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"",
            HoconAddMode.Prepend);
    }

    // A child may read the worktree, attended or not (D2), so the reviewed
    // phrase runs with no project declaration and no prompt.
    [Fact]
    public async Task Subagent_interactive_reviewed_phrase_in_readable_worktree_needs_no_prompt()
    {
        const string callId = "call-project-scope-approval";
        var approvalBridge = new ConsentCapableBridge();
        var scenario = await RunProjectScopeScenarioAsync(
            CreateShellTool(),
            callId,
            ProjectScopeCommand,
            approvalBridge);

        Assert.True(scenario.Result.Success, scenario.Result.Output);
        Assert.Contains(
            scenario.Worktree,
            GetLastToolResult(scenario.Client, callId),
            StringComparison.OrdinalIgnoreCase);
    }

    [SlopwatchSuppress("SW001", "This regression requires a POSIX shell cwd and Bash project-scope correction behavior.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The project-scope correction defines Bash path behavior.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Subagent_project_declaration_updates_child_prompt_before_unchanged_retry(
        bool supportsApproval)
    {
        const string firstCallId = "call-project-scope-first";
        const string declarationCallId = "call-project-scope-declare";
        const string retryCallId = "call-project-scope-retry";
        const string projectGuidance = "Project instructions: use the local test conventions.";
        var sessionDirectory = TestPath("sessions", "project-scope-child");
        var worktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var workspacesDirectory = Directory.GetParent(worktree)!.FullName;
        var shell = CreateShellTool();
        var setWorkingDirectory = new SetWorkingDirectoryTool(
            new ToolConfig(),
            new NetclawPaths(workspacesDirectory, workspacesDirectory),
            new ToolPathPolicy([]));
        var client = new SequencedToolCallChatClient(
        [
            ProjectScopeCall(
                firstCallId,
                worktree,
                TestShellEnvironment.PrintWorkingDirectoryCommand),
            new FunctionCallContent(
                declarationCallId,
                SetWorkingDirectoryTool.ToolName,
                new Dictionary<string, object?>
                {
                    ["Path"] = worktree,
                    ["_rationale"] = "Declare the project directory before the next inspection."
                }),
            ProjectScopeCall(
                retryCallId,
                worktree,
                TestShellEnvironment.PrintWorkingDirectoryCommand)
        ]);
        var approvalBridge = supportsApproval
            ? new ConsentCapableBridge()
            : null;
        var actor = Sys.ActorOf(SubAgentActor.CreatePropsWithProjectInstructionProvider(
            CreateDefinition([shell, setWorkingDirectory]),
            client,
            CreateProjectScopePolicy(workspacesDirectory),
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

        // D2: an attended and an unattended child get the same result.
        Assert.True(result.Success, result.Output);
        Assert.Contains(
            worktree,
            GetLastToolResult(client.LastReceivedMessages, retryCallId),
            StringComparison.Ordinal);
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
            var merged = WorkingContextUpdater.MergeSuccessfulSubAgentWorkingContext(parent, result.Completion);
            Assert.Equal("/parent/project", merged.ProjectDirectory);
        }
    }

    private static ShellTool CreateShellTool()
    {
        var environment = TestShellEnvironment.Current;
        return new ShellTool(
            new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
            new ToolPathPolicy(environment, []),
            new ShellCommandPolicy(environment));
    }

    private async Task<ProjectScopeScenario> RunProjectScopeScenarioAsync(
        INetclawTool shell,
        string callId,
        string shellCommand,
        IParentApprovalBridge? approvalBridge)
    {
        var worktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var client = new FakeChatClient
        {
            ToolCallsOnFirstCall = [ProjectScopeCall(callId, worktree, shellCommand)]
        };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(
            CreateDefinition([shell]),
            client,
            CreateProjectScopePolicy(worktree)));
        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Scope = SubAgentTestScope.Create(approvalBridge: approvalBridge),
                Task = "Inspect project metrics.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            ApprovalAskTimeout,
            TestContext.Current.CancellationToken);

        return new ProjectScopeScenario(result, client, worktree);
    }

    private sealed record ProjectScopeScenario(
        SubAgentResult Result,
        FakeChatClient Client,
        string Worktree);

    private static ToolAccessPolicy CreateProjectScopePolicy(string workspacesDirectory)
    {
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [ShellTool.ToolName] = ToolApprovalMode.Approval,
                [SetWorkingDirectoryTool.ToolName] = ToolApprovalMode.Auto
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
            new NetclawPaths(workspacesDirectory, workspacesDirectory),
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(environment),
            new ToolPathPolicy(environment, []),
            safeVerbs: SafeVerbList.FromVerbs(approvalShell, safeVerbs));
    }

    private static FunctionCallContent ProjectScopeCall(
        string callId,
        string workingDirectory,
        string command)
        => new(callId, ShellTool.ToolName, new Dictionary<string, object?>
        {
            ["Command"] = command,
            ["WorkingDirectory"] = workingDirectory,
            ["_rationale"] = "Inspect the project metric sources."
        });

    private static string ProjectScopeCommand =>
        TestShellEnvironment.Current.Grammar == ShellGrammar.Bash
            ? "pwd"
            : "Get-Location";
}
