// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public class SubAgentSpawnIntegrationTests : LlmSessionTestBase
{
    private const string MainIdentityMarker = "You are a test assistant with subagent support.";
    private const string OperatingRulesMarker = "[embedded agents] Sub-agents inherit operating rules.";
    private const string AgentsLayerMarker = "[agents] This marker should never appear in routed subagent calls.";

    private readonly RecordingRoleChatClientProvider _clientProvider = new();
    private RecordingContextTool? _recordingFileReadTool;
    private RecordingContextTool? _recordingShellTool;

    public SubAgentSpawnIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        var promptProvider = new TestSystemPromptProvider(MainIdentityMarker, OperatingRulesMarker);
        services.AddSingleton<IChatClientProvider>(_clientProvider);
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(promptProvider);
        services.AddSingleton<IReadOnlyList<IContextLayerProvider>>(
        [
            new StaticContextLayerProvider(AgentsLayerMarker, ContextLayerTiming.OnceAtStart)
        ]);

        var skillRoot = Path.Combine(Path.GetTempPath(), $"netclaw-skill-routing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(skillRoot);

        var routedSkillDir = Path.Combine(skillRoot, "ops-route");
        Directory.CreateDirectory(routedSkillDir);
        var routedSkillFile = Path.Combine(routedSkillDir, "SKILL.md");
        File.WriteAllText(routedSkillFile, """
            ---
            name: ops-route
            description: Route to operations helper.
            metadata:
              subagent: summarizer
            ---

            # Ops Route

            You specialize in daemon health checks.
            """);

        var missingSkillDir = Path.Combine(skillRoot, "missing-route");
        Directory.CreateDirectory(missingSkillDir);
        var missingSkillFile = Path.Combine(missingSkillDir, "SKILL.md");
        File.WriteAllText(missingSkillFile, """
            ---
            name: missing-route
            description: Route to a missing subagent.
            metadata:
              subagent: does-not-exist
            ---

            # Missing Route
            """);

        var restrictiveSkillDir = Path.Combine(skillRoot, "ops-route-restrictive");
        Directory.CreateDirectory(restrictiveSkillDir);
        var restrictiveSkillFile = Path.Combine(restrictiveSkillDir, "SKILL.md");
        File.WriteAllText(restrictiveSkillFile, """
            ---
            name: ops-route-restrictive
            description: Route to operations helper with restrictive allowed-tools metadata.
            allowed-tools: web_fetch
            metadata:
              subagent: summarizer
            ---

            # Ops Route Restrictive

            You specialize in daemon health checks.
            """);

        var skillRegistry = new SkillRegistry();
        skillRegistry.Register(new SkillEntry("ops-route", "Ops Route", "Route to operations helper.", routedSkillFile, routedSkillDir, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "summarizer"
        });
        skillRegistry.Register(new SkillEntry("missing-route", "Missing Route", "Route to missing subagent.", missingSkillFile, missingSkillDir, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "does-not-exist"
        });
        skillRegistry.Register(new SkillEntry("ops-route-restrictive", "Ops Route Restrictive", "Route to operations helper with restrictive metadata.", restrictiveSkillFile, restrictiveSkillDir, null)
        {
            HasSubagentRoutingMetadata = true,
            Subagent = "summarizer",
            AllowedTools = "web_fetch"
        });
        services.AddSingleton(skillRegistry);

        var registry = new ToolRegistry();
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var toolAccessPolicy = new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(ShellExecutionEnvironment.Current));
        var subAgentRegistry = new SubAgentDefinitionRegistry();
        var subAgentPaths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-subagents-{Guid.NewGuid():N}"));
        subAgentPaths.EnsureDirectoriesExist();
        subAgentRegistry.Register(new SubAgentProfile
        {
            Name = "summarizer",
            Description = "Summarize content",
            SystemPrompt = "You are a summarizer.",
            ToolNames = ["file_read"],
            ModelRole = ModelRole.Compaction,
            Visibility = SubAgentVisibility.UserFacing,
            EmitStructuredFindings = false
        });
        subAgentRegistry.Register(new SubAgentProfile
        {
            Name = "sheller",
            Description = "Run approved shell commands",
            SystemPrompt = "You run shell commands when approved.",
            ToolNames = ["shell_execute"],
            ModelRole = ModelRole.Compaction,
            Visibility = SubAgentVisibility.UserFacing,
            EmitStructuredFindings = false
        });

        var spawner = new SubAgentSpawner(
            _clientProvider,
            registry,
            toolAccessPolicy,
            approvalService: null,
            promptProvider,
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                new ExecutionEnvironmentInspector(ShellExecutionEnvironment.Current),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkingContextSnapshotProvider>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SubAgentSpawner>.Instance);

        registry.Register(new SpawnAgentTool(subAgentRegistry, spawner, subAgentPaths));
        _recordingFileReadTool = new RecordingContextTool("file_read", "stub file content", "file");
        registry.Register(_recordingFileReadTool);
        _recordingShellTool = new RecordingContextTool("shell_execute", "shell ok", "shell");
        registry.Register(_recordingShellTool);

        services.AddSingleton(registry);
        services.AddSingleton(subAgentRegistry);
        services.AddSingleton(spawner);
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(
            registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatchingToolExecutor>.Instance));
    }

    [Fact]
    public async Task Spawn_agent_runs_under_session_and_emits_subagent_events()
    {
        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-spawn",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Summarize src/README.md"
                })
        ];

        var sessionId = new SessionId("console/subagent-integration");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a subagent to summarize the file",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var toolCall = await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("spawn_agent", toolCall.ToolName.Value);

        var started = await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentPhase.Started, started.Phase);
        Assert.Equal("summarizer", started.AgentName.Value);
        Assert.Equal(2, started.ToolCount);

        var completed = await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.Equal("summarizer", completed.AgentName.Value);
        Assert.True(completed.Success);
        Assert.Equal(0, completed.FindingsCount);
        Assert.Null(completed.MemoryDecision);

        // Drain the tool result output for spawn_agent emitted after tool execution
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _clientProvider.Main.CallCount);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);

        var subagentCall = Assert.Single(_clientProvider.Compaction.ReceivedMessages);
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && m.Text.Contains(OperatingRulesMarker, StringComparison.Ordinal)
            && m.Text.Contains("You are a summarizer.", StringComparison.Ordinal)
            && m.Text.Contains("headless, non-interactive worker", StringComparison.Ordinal));
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User
            && (m.Text?.Contains("execution_environment:", StringComparison.Ordinal) ?? false)
            && (m.Text?.Contains("Task:\nSummarize src/README.md", StringComparison.Ordinal) ?? false));
        Assert.DoesNotContain(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System && (m.Text?.Contains("test assistant with subagent support", StringComparison.Ordinal) ?? false));
        Assert.DoesNotContain(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User && (m.Text?.Contains("Use a subagent to summarize the file", StringComparison.Ordinal) ?? false));

        // The session actor must thread a SessionScopedChatOptions carrier so the
        // chat-client decorators can correlate LLM diagnostics to the session. The
        // sub-agent call carries the *parent* session id (collapsing the scope suffix),
        // so both the main turn and the sub-agent's LLM calls correlate to one session.
        var mainOptions = Assert.IsType<SessionScopedChatOptions>(_clientProvider.Main.ReceivedOptions[^1]);
        Assert.Equal(sessionId.Value, mainOptions.SessionId);
        var subagentOptions = Assert.IsType<SessionScopedChatOptions>(_clientProvider.Compaction.ReceivedOptions[^1]);
        Assert.Equal(sessionId.Value, subagentOptions.SessionId);
    }

    [Fact]
    public async Task Spawn_agent_subagent_approval_uses_parent_authority_and_resumes_after_approval()
    {
        const string parentCallId = "call_5aaea0c7afec4e47bbc062d8";
        const string childCallId = "call_6f11cdf0c19746c59e778331";

        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                parentCallId,
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "sheller",
                    ["task"] = "Push the current branch"
                })
        ];
        _clientProvider.Compaction.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                childCallId,
                "shell_execute",
                new Dictionary<string, object?>
                {
                    ["Command"] = "git push origin main",
                    // Per-call timeout hint on the sub-agent path: the sub-agent
                    // loop must extract this via the shared executor seam and apply
                    // it to the tool context (it previously skipped extraction and
                    // silently dropped the hint).
                    ["_timeout_seconds"] = 1800
                })
        ];

        var sessionId = new SessionId("console/subagent-approval-integration");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-approval-events");
        var source = BuildPersonalSource();

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a subagent to push the branch",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var toolCall = await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("spawn_agent", toolCall.ToolName.Value);

        var started = await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentPhase.Started, started.Phase);

        var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(childCallId, request.CallId.Value);
        Assert.StartsWith($"{parentCallId}/subagent-approval/", request.CallId.Value, StringComparison.Ordinal);
        Assert.Contains("subagent-approval", request.CallId.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(childCallId, request.CallId.Value, StringComparison.Ordinal);
        AssertApprovalButtonValuesRoundTrip(request);
        Assert.Equal("shell_execute", request.ToolName.Value);
        Assert.Equal(source.SenderId, request.RequesterSenderId);
        Assert.Equal(source.Principal, request.RequesterPrincipal);
        Assert.Contains(request.Options, o => o.Key.Value == ApprovalOptionKeys.ApproveOnce);

        var approvalReply = await sessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = request.CallId,
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = source.SenderId!
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(approvalReply);

        var completed = await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.True(completed.Success);

        var result = await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("spawn_agent", result.ToolName.Value);

        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(_recordingShellTool);
        Assert.True(_recordingShellTool!.WasCalled);
        Assert.Equal(TrustAudience.Personal, _recordingShellTool.LastContext?.Audience);

        // The sub-agent extracted the meta timeout hint and applied it to the
        // tool context (regression guard for the previously-dropped hint).
        Assert.Equal(TimeSpan.FromSeconds(1800), _recordingShellTool.LastContext?.ExecutionTimeout.Value);
    }

    [Fact]
    public async Task Spawn_agent_subagent_approval_expires_after_parent_session_recovery()
    {
        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-spawn-shell-expire",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "sheller",
                    ["task"] = "Push the current branch"
                })
        ];
        _clientProvider.Compaction.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-subagent-shell-expire",
                "shell_execute",
                new Dictionary<string, object?>
                {
                    ["Command"] = "git push origin main"
                })
        ];

        var sessionId = new SessionId("console/subagent-approval-expired");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-approval-expired-events");
        var source = BuildPersonalSource();

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a subagent to push the branch",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("subagent-approval", request.CallId.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("call-subagent-shell-expire", request.CallId.Value, StringComparison.Ordinal);
        AssertApprovalButtonValuesRoundTrip(request);
        Assert.False(_recordingShellTool!.WasCalled);

        await ColdRespawnAsync(sessionId);

        var subscriberB = CreateTestProbe("subagent-approval-expired-events-b");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var reply = await sessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = request.CallId,
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = source.SenderId!
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var nack = Assert.IsType<CommandNack>(reply);
        Assert.Equal(ApprovalNackReasons.PromptExpired, nack.Reason);
        var notice = await subscriberB.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("expired", notice.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(_recordingShellTool.WasCalled);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Never mind, just say hello",
            Source = source
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await subscriberB.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriberB.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var resumedCall = _clientProvider.Main.ReceivedMessages[^1];
        Assert.Contains(resumedCall, message =>
            message.Role == Microsoft.Extensions.AI.ChatRole.Tool
            && message.Contents.OfType<FunctionResultContent>().Any(result =>
                result.CallId == "call-spawn-shell-expire"
                && result.Result?.ToString()?.Contains("session restarted", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public async Task Routed_slash_command_with_unknown_subagent_fails_loud_without_inline_fallback()
    {
        var sessionId = new SessionId("test-channel/routed-slash-missing");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-missing-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/missing-route check health"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("not registered", text.Text, StringComparison.OrdinalIgnoreCase);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Skipped, completed.Outcome);

        Assert.Equal(0, _clientProvider.Main.CallCount);
        Assert.Equal(0, _clientProvider.Compaction.CallCount);
    }

    [Fact]
    public async Task Routed_slash_command_executes_with_overlay_and_isolated_prompt_stack()
    {
        var sessionId = new SessionId("test-channel/routed-slash-success");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-success-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check daemon health",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var text = await ExpectTextOutputAsync(subscriber, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        await ExpectTurnCompletedAsync(subscriber, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(0, _clientProvider.Main.CallCount);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);

        var subagentCall = Assert.Single(_clientProvider.Compaction.ReceivedMessages);
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains("You are a summarizer.", StringComparison.Ordinal) ?? false));
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains("[Skill Overlay]", StringComparison.Ordinal) ?? false));
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains("You specialize in daemon health checks.", StringComparison.Ordinal) ?? false));
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User
            && (m.Text?.Contains("execution_environment:", StringComparison.Ordinal) ?? false)
            && (m.Text?.Contains("Task:\ncheck daemon health", StringComparison.Ordinal) ?? false));
        Assert.DoesNotContain(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains(MainIdentityMarker, StringComparison.Ordinal) ?? false));
        Assert.DoesNotContain(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.System
            && (m.Text?.Contains(AgentsLayerMarker, StringComparison.Ordinal) ?? false));
    }

    // NOTE: routing the spawn lifecycle to session.log is no longer per-path-wired — the
    // breadcrumbs log under a SessionId scope and the file-logger partitions them regardless of
    // which path (tool-execution or routed-skill) drove the spawn. The producer side is covered
    // by SubAgentSpawnObservabilityTests; the routing by RollingFileLoggerPartitionTests.

    [Fact]
    public async Task Reminder_sourced_slash_command_routes_like_normal_slash_dispatch()
    {
        var sessionId = new SessionId("test-channel/routed-slash-reminder");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-reminder-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check scheduled health",
            Source = BuildReminderSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await ExpectTextOutputAsync(subscriber, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(subscriber, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(0, _clientProvider.Main.CallCount);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);

        var subagentCall = Assert.Single(_clientProvider.Compaction.ReceivedMessages);
        Assert.Contains(subagentCall, m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User
            && string.Equals(m.Text, "check scheduled health", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reminder_sourced_routed_slash_duplicate_is_deduped()
    {
        var sessionId = new SessionId("test-channel/routed-slash-reminder-dedup");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-reminder-dedup-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var reminderSource = BuildReminderSource("ops-route:1712000000000");

        var firstAck = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check scheduled health",
            Source = reminderSource
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, firstAck.SessionId);

        await ExpectTextOutputAsync(subscriber, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(subscriber, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var callsAfterFirst = _clientProvider.Compaction.CallCount;

        var duplicateAck = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check scheduled health",
            Source = reminderSource
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, duplicateAck.SessionId);

        await AwaitAssertAsync(() =>
        {
            Assert.Equal(callsAfterFirst, _clientProvider.Compaction.CallCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reminder_sourced_routed_slash_duplicate_is_deduped_while_first_execution_in_flight()
    {
        var sessionId = new SessionId("test-channel/routed-slash-reminder-dedup-inflight");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-reminder-dedup-inflight-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var reminderSource = BuildReminderSource("ops-route:1712000000001");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientProvider.Compaction.NextResponseGate = gate;

        var firstAck = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check scheduled health",
            Source = reminderSource
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, firstAck.SessionId);

        await AwaitAssertAsync(() =>
        {
            Assert.Equal(1, _clientProvider.Compaction.CallCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);

        var callsWhileBlocked = _clientProvider.Compaction.CallCount;

        var duplicateAck = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check scheduled health",
            Source = reminderSource
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, duplicateAck.SessionId);

        await AwaitAssertAsync(() =>
        {
            Assert.Equal(callsWhileBlocked, _clientProvider.Compaction.CallCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);

        gate.TrySetResult();

        await ExpectTextOutputAsync(subscriber, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(subscriber, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await AwaitAssertAsync(() =>
        {
            Assert.Equal(callsWhileBlocked, _clientProvider.Compaction.CallCount);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Routed_slash_ignores_skill_allowed_tools_for_runtime_authorization_and_inherits_audience()
    {
        _clientProvider.Compaction.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                "call-read",
                "file_read",
                new Dictionary<string, object?>
                {
                    ["Path"] = "README.md"
                })
        ];

        var sessionId = new SessionId("test-channel/routed-slash-restrictive");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-restrictive-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var source = BuildReminderSource();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route-restrictive run health check",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var text = await ExpectTextOutputAsync(subscriber, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(subscriber, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _clientProvider.Main.CallCount);
        Assert.Equal(2, _clientProvider.Compaction.CallCount);
        Assert.NotNull(_recordingFileReadTool);
        Assert.True(_recordingFileReadTool!.WasCalled);
        Assert.Equal(TrustAudience.Team, _recordingFileReadTool.LastContext?.Audience);
        Assert.Equal(source.Boundary, _recordingFileReadTool.LastContext?.Boundary);
    }

    private static MessageSource BuildPersonalSource()
    {
        return new MessageSource
        {
            ChannelType = ChannelType.Tui,
            SenderId = new SenderId("test-user"),
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromChannelType(ChannelType.Tui.ToWireValue(), TrustAudience.Personal),
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
            ReceivedAt = DateTimeOffset.UtcNow
        };
    }

    private static MessageSource BuildReminderSource(string? reminderId = null)
    {
        return new MessageSource
        {
            ChannelType = ChannelType.Reminder,
            SenderId = new SenderId("reminder-executor"),
            Audience = TrustAudience.Team,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromChannelType(ChannelType.Reminder.ToWireValue(), TrustAudience.Team),
            Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
            ReceivedAt = DateTimeOffset.UtcNow,
            ReminderId = reminderId is null ? null : new ReminderId(reminderId)
        };
    }

    private static async Task<TextOutput> ExpectTextOutputAsync(Akka.TestKit.TestProbe probe, TimeSpan timeout, CancellationToken ct)
    {
        for (var i = 0; i < 8; i++)
        {
            var msg = await probe.ExpectMsgAsync<SessionOutput>(timeout, cancellationToken: ct);
            if (msg is TextOutput text)
                return text;
        }

        throw new Xunit.Sdk.XunitException("Expected TextOutput but only received non-text session outputs.");
    }

    private static async Task<TurnCompleted> ExpectTurnCompletedAsync(Akka.TestKit.TestProbe probe, TimeSpan timeout, CancellationToken ct)
    {
        for (var i = 0; i < 8; i++)
        {
            var msg = await probe.ExpectMsgAsync<SessionOutput>(timeout, cancellationToken: ct);
            if (msg is TurnCompleted completed)
                return completed;
        }

        throw new Xunit.Sdk.XunitException("Expected TurnCompleted but only received other session outputs.");
    }

    private async Task ColdRespawnAsync(SessionId sessionId)
    {
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static void AssertApprovalButtonValuesRoundTrip(ToolInteractionRequest request)
    {
        foreach (var option in request.Options)
        {
            var encoded = ApprovalButtonValueCodec.Encode(request, option);
            Assert.True(
                encoded.Length <= ApprovalButtonValueCodec.MaxEncodedLength,
                $"Approval button value exceeded {ApprovalButtonValueCodec.MaxEncodedLength} chars: {encoded.Length}");
            Assert.True(ApprovalButtonValueCodec.TryDecode(encoded, out var callId, out var selectedKey, out var requesterSenderId));
            Assert.Equal(request.CallId.Value, callId);
            Assert.Equal(option.Key.Value, selectedKey);
            Assert.Equal(request.RequesterSenderId?.Value, requesterSenderId);
        }
    }

    private sealed class RecordingRoleChatClientProvider : IChatClientProvider
    {
        public FakeChatClient Main { get; } = new();
        public FakeChatClient Compaction { get; } = new();

        public IChatClient GetClient(ModelRole role)
            => role == ModelRole.Compaction ? Compaction : Main;
    }

    private sealed class RecordingContextTool(string name, string result, string grantCategory = "builtin") : INetclawTool
    {
        public string Name { get; } = name;
        public LlmFacingToolName LlmFacingName { get; } = LlmFacingToolName.FromCanonical(name);
        public string Description => "Recording fake tool";
        public string GrantCategory { get; } = grantCategory;
        public System.Text.Json.JsonElement ParameterSchema => default;

        public bool WasCalled { get; private set; }
        public ToolInvocationContext? LastContext { get; private set; }

        public AITool ToAITool() => AIFunctionFactory.Create(() => result, name: Name, description: Description);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => ExecuteAsync(arguments, TestToolExecutionContext.CreateUnbound().Invocation, ct);

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
        {
            WasCalled = true;
            LastContext = context;
            return Task.FromResult(result);
        }
    }

    private sealed class StaticContextLayerProvider(string content, ContextLayerTiming timing) : IContextLayerProvider
    {
        public ContextLayerTiming Timing => timing;

        public string GetContextLayer(TrustAudience audience) => content;
    }

    private sealed class TestSystemPromptProvider(string systemPrompt, string operatingRules) : ISystemPromptProvider
    {
        public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null) => systemPrompt;

        public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory) => null;

        public string? GetOperatingRules(TrustAudience audience)
            => audience == TrustAudience.Public ? null : operatingRules;
    }
}
