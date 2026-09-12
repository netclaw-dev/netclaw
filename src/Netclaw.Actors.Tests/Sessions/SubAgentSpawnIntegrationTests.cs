// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public class SubAgentSpawnIntegrationTests : LlmSessionTestBase
{
    private const string ApprovalProbeToolName = "approval_probe";
    private const string HiddenSpecialtyToolName = "hidden_specialty";
    private const string MainIdentityMarker = "You are a test assistant with subagent support.";
    private const string OperatingRulesMarker = "[embedded agents] Sub-agents inherit operating rules.";
    private const string AgentsLayerMarker = "[agents] This marker should never appear in routed subagent calls.";
    private const string ToolFootprintScenario = "personal-session-with-fixed-synthetic-mcp-catalog";
    private const string ToolFootprintMetric =
        "Compact UTF-8 JSON array of final function names, descriptions, and input schemas in ChatOptions.Tools order.";
    private const string SyntheticMcpServerName = "synthetic_catalog";
    private const int SyntheticMcpToolCount = 200;

    private readonly RecordingRoleChatClientProvider _clientProvider = new();
    private readonly ToolRegistry _toolRegistry = new();
    private RecordingContextTool? _recordingFileReadTool;
    private RecordingContextTool? _recordingApprovalTool;
    private RecordingContextTool? _recordingHiddenTool;

    private static FunctionCallContent CreateToolCall(
        string callId,
        string name,
        IDictionary<string, object?> arguments)
    {
        var callArguments = new Dictionary<string, object?>(arguments, StringComparer.Ordinal)
        {
            ["_rationale"] = "Verify the sub-agent session behavior."
        };
        return new FunctionCallContent(callId, name, callArguments);
    }

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

        var registry = _toolRegistry;
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [ApprovalProbeToolName] = ToolApprovalMode.Approval,
                [HiddenSpecialtyToolName] = ToolApprovalMode.Deny
            }
        };
        var toolAccessPolicy = new ToolAccessPolicy(new NetclawPaths(),
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));
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
            Name = "approval-tester",
            Description = "Test an approval request",
            SystemPrompt = "You request approval for the test tool.",
            ToolNames = [ApprovalProbeToolName],
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
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkingContextSnapshotProvider>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SubAgentSpawner>.Instance);

        // This fixture drives spawn_agent directly from the main model. Mark only that
        // test seam as Core; production registration keeps spawn_agent deferred.
        registry.RegisterCore(new SpawnAgentTool(subAgentRegistry, spawner, subAgentPaths));
        registry.RegisterCore(new SearchToolsTool(registry, toolAccessPolicy));
        registry.RegisterCore(new LoadToolTool(registry, toolAccessPolicy));
        registry.RegisterCore(new AttachFileTool(toolConfig, subAgentPaths, new ToolPathPolicy([])));
        _recordingFileReadTool = new RecordingContextTool("file_read", "stub file content", "file");
        registry.RegisterCore(_recordingFileReadTool);
        _recordingApprovalTool = new RecordingContextTool(ApprovalProbeToolName, "approval ok");
        registry.Register(_recordingApprovalTool);
        _recordingHiddenTool = new RecordingContextTool(HiddenSpecialtyToolName, "hidden result");
        registry.Register(_recordingHiddenTool);

        services.AddSingleton(registry);
        services.AddSingleton(subAgentRegistry);
        services.AddSingleton(spawner);
        services.AddSingleton<IToolExecutor>(new DispatchingToolExecutor(
            registry,
            toolAccessPolicy,
            approvalService: null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatchingToolExecutor>.Instance));
    }

    [Fact]
    public async Task Spawn_agent_runs_under_session_and_emits_subagent_events()
    {
        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            CreateToolCall(
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
        Assert.Equal(4, started.ToolCount);

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
        var subagentTask = Assert.Single(
            subagentCall,
            static message => message.Role == Microsoft.Extensions.AI.ChatRole.User);
        Assert.Contains("Context:\n[working-context]", subagentTask.Text, StringComparison.Ordinal);
        Assert.Contains("platform: Linux", subagentTask.Text, StringComparison.Ordinal);
        Assert.Contains("executable: /bin/bash", subagentTask.Text, StringComparison.Ordinal);
        Assert.EndsWith("Task:\nSummarize src/README.md", subagentTask.Text, StringComparison.Ordinal);
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
        var mainToolNames = _clientProvider.Main.ReceivedToolNames[0]
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();
        var childToolNames = _clientProvider.Compaction.ReceivedToolNames[0]
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            mainToolNames.Where(static name => name is not "attach_file" and not "spawn_agent"),
            childToolNames);
        Assert.Contains("attach_file", mainToolNames);
        Assert.DoesNotContain("attach_file", childToolNames);
    }

    [Fact]
    public async Task Final_model_visible_child_footprint_reduces_from_frozen_baseline()
    {
        RegisterSyntheticMcpCatalog();
        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            CreateToolCall(
                "call-footprint-spawn",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Summarize the fixed synthetic catalog."
                })
        ];

        var sessionId = new SessionId("console/tool-footprint-baseline");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("tool-footprint-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use the summarizer.",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(
            subscriber,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var subagentOptions = Assert.IsType<SessionScopedChatOptions>(_clientProvider.Compaction.ReceivedOptions[0]);
        var subagentTools = Assert.IsAssignableFrom<IEnumerable<AITool>>(subagentOptions.Tools);

        var actual = ModelVisibleToolFootprintCalculator.Measure(subagentTools);
        var baseline = await ReadToolFootprintBaselineAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Equal(ToolFootprintScenario, baseline.Scenario);
        Assert.Equal(ToolFootprintMetric, baseline.Metric);
        Assert.Equal(SyntheticMcpToolCount, baseline.SyntheticMcpToolCount);
        Assert.True(actual.Count < baseline.SubagentFull.Count);
        Assert.True(
            actual.SerializedDefinitionBytes
            < baseline.SubagentFull.SerializedDefinitionBytes);
    }

    [Fact]
    public async Task Child_loads_one_deferred_tool_without_exposing_the_catalog()
    {
        RegisterSyntheticMcpCatalog();
        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            CreateToolCall(
                "call-progressive-spawn",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Use one synthetic lookup tool."
                })
        ];
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-progressive-search",
                "search_tools",
                new Dictionary<string, object?>
                {
                    ["Query"] = "tool_007"
                })
        ]);
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-progressive-load",
                "load_tool",
                new Dictionary<string, object?>
                {
                    ["Name"] = $"{SyntheticMcpServerName}/tool_007"
                })
        ]);

        var sessionId = new SessionId("console/subagent-progressive-disclosure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-progressive-disclosure-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use one deferred child tool.",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(
            subscriber,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var calls = _clientProvider.Compaction.ReceivedOptions;
        Assert.True(calls.Count >= 3);
        var initialNames = Assert.IsAssignableFrom<IEnumerable<AITool>>(calls[0]?.Tools)
            .OfType<AIFunctionDeclaration>()
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();
        var afterSearchNames = Assert.IsAssignableFrom<IEnumerable<AITool>>(calls[1]?.Tools)
            .OfType<AIFunctionDeclaration>()
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();
        var loadedNames = Assert.IsAssignableFrom<IEnumerable<AITool>>(calls[2]?.Tools)
            .OfType<AIFunctionDeclaration>()
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["file_read", "load_tool", "search_tools"], initialNames);
        Assert.Equal(initialNames, afterSearchNames);
        Assert.Equal(
            ["file_read", "load_tool", "search_tools", "synthetic_catalog__tool_007"],
            loadedNames);
        Assert.DoesNotContain(loadedNames, static name => name == "spawn_agent");
        var searchResult = GetToolResult(
            _clientProvider.Compaction.ReceivedMessages[1],
            "call-progressive-search");
        Assert.Contains("synthetic_catalog__tool_007", searchResult, StringComparison.Ordinal);

        var firstMessages = _clientProvider.Compaction.ReceivedMessages[0];
        var system = Assert.Single(
            firstMessages,
            static message => message.Role == Microsoft.Extensions.AI.ChatRole.System);
        Assert.Contains("synthetic_catalog (200 tools)", system.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("spawn_agent", system.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("tool_007", system.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Child_cannot_discover_load_or_dispatch_recursive_or_hidden_tools()
    {
        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            CreateToolCall(
                "call-policy-spawn",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Check unavailable capabilities."
                })
        ];
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-search-recursive-exact",
                "search_tools",
                new Dictionary<string, object?> { ["Query"] = "spawn_agent" }),
            CreateToolCall(
                "call-search-recursive-fuzzy",
                "search_tools",
                new Dictionary<string, object?> { ["Query"] = "spwn agnt" }),
            CreateToolCall(
                "call-load-recursive",
                "load_tool",
                new Dictionary<string, object?> { ["Name"] = "spawn_agent" }),
            CreateToolCall(
                "call-dispatch-recursive",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Try recursive delegation."
                }),
            CreateToolCall(
                "call-search-hidden",
                "search_tools",
                new Dictionary<string, object?> { ["Query"] = HiddenSpecialtyToolName }),
            CreateToolCall(
                "call-load-hidden",
                "load_tool",
                new Dictionary<string, object?> { ["Name"] = HiddenSpecialtyToolName }),
            CreateToolCall(
                "call-search-attach",
                "search_tools",
                new Dictionary<string, object?> { ["Query"] = "attach_file" }),
            CreateToolCall(
                "call-load-attach",
                "load_tool",
                new Dictionary<string, object?> { ["Name"] = "attach_file" }),
            CreateToolCall(
                "call-dispatch-attach",
                "attach_file",
                new Dictionary<string, object?> { ["Path"] = "report.txt" })
        ]);

        var sessionId = new SessionId("console/subagent-policy-boundaries");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-policy-boundary-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a child to check unavailable tools.",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(
            subscriber,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var resultMessages = _clientProvider.Compaction.ReceivedMessages[1];
        Assert.StartsWith(
            "No tools found matching",
            GetToolResult(resultMessages, "call-search-recursive-exact"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "No tools found matching",
            GetToolResult(resultMessages, "call-search-recursive-fuzzy"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Tool 'spawn_agent' not found.",
            GetToolResult(resultMessages, "call-load-recursive"),
            StringComparison.Ordinal);
        Assert.Equal(
            "Unknown tool: spawn_agent",
            GetToolResult(resultMessages, "call-dispatch-recursive"));
        Assert.StartsWith(
            "No tools found matching",
            GetToolResult(resultMessages, "call-search-hidden"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            $"Tool '{HiddenSpecialtyToolName}' not found.",
            GetToolResult(resultMessages, "call-load-hidden"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "No tools found matching",
            GetToolResult(resultMessages, "call-search-attach"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Tool 'attach_file' not found.",
            GetToolResult(resultMessages, "call-load-attach"),
            StringComparison.Ordinal);
        Assert.Equal(
            "Unknown tool: attach_file",
            GetToolResult(resultMessages, "call-dispatch-attach"));

        Assert.NotNull(_recordingHiddenTool);
        Assert.False(_recordingHiddenTool!.WasCalled);
        Assert.All(
            _clientProvider.Compaction.ReceivedToolNames,
            names =>
            {
                Assert.DoesNotContain("spawn_agent", names);
                Assert.DoesNotContain("attach_file", names);
                Assert.DoesNotContain(HiddenSpecialtyToolName, names);
                Assert.Equal(
                    ["file_read", "load_tool", "search_tools"],
                    names.OrderBy(static name => name, StringComparer.Ordinal));
            });
    }

    [Fact]
    public async Task Child_shell_correction_does_not_expose_statically_denied_tools()
    {
        var shellProbe = new RecordingContextTool(ShellTool.ToolName, "shell must not run", "shell");
        _toolRegistry.Register(shellProbe);
        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            CreateToolCall(
                "call-denied-native-spawn",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Check denied native tool names through the shell."
                })
        ];
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-shell-attach",
                ShellTool.ToolName,
                new Dictionary<string, object?> { ["command"] = "attach_file" }),
            CreateToolCall(
                "call-shell-spawn",
                ShellTool.ToolName,
                new Dictionary<string, object?> { ["command"] = "spawn_agent" })
        ]);
        _clientProvider.Compaction.PlannedResponses.Enqueue([new TextContent("Child completed.")]);

        var sessionId = new SessionId("console/subagent-denied-native-correction");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-denied-native-correction-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);

        var source = BuildPersonalSource();
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a child to check denied native tool names.",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SubAgentOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 2; i++)
        {
            var approval = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
                TimeSpan.FromSeconds(3),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ShellTool.ToolName, approval.ToolName.Value);
            var denied = await sessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
            {
                SessionId = sessionId,
                CallId = approval.CallId,
                SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.Deny),
                SenderId = source.SenderId!
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.IsType<CommandAck>(denied);
        }
        await ExpectTurnCompletedAsync(
            subscriber,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var resultMessages = _clientProvider.Compaction.ReceivedMessages[1];
        Assert.DoesNotContain(
            "native Netclaw tool",
            GetToolResult(resultMessages, "call-shell-attach"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "native Netclaw tool",
            GetToolResult(resultMessages, "call-shell-spawn"),
            StringComparison.Ordinal);
        Assert.False(shellProbe.WasCalled);
        Assert.All(
            _clientProvider.Compaction.ReceivedToolNames,
            names =>
            {
                Assert.DoesNotContain("attach_file", names);
                Assert.DoesNotContain("spawn_agent", names);
            });
    }

    [Fact]
    public async Task Loaded_child_tool_does_not_transfer_to_the_next_child()
    {
        RegisterSyntheticMcpCatalog();
        _clientProvider.Main.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-isolation-spawn-1",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Use one synthetic tool."
                })
        ]);
        _clientProvider.Main.PlannedResponses.Enqueue([new TextContent("First child completed.")]);
        _clientProvider.Main.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-isolation-spawn-2",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Start without inherited tools."
                })
        ]);
        _clientProvider.Main.PlannedResponses.Enqueue([new TextContent("Second child completed.")]);
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-isolation-load",
                "load_tool",
                new Dictionary<string, object?>
                {
                    ["Name"] = $"{SyntheticMcpServerName}/tool_007"
                })
        ]);
        _clientProvider.Compaction.PlannedResponses.Enqueue([new TextContent("Loaded child completed.")]);
        _clientProvider.Compaction.PlannedResponses.Enqueue([new TextContent("Fresh child completed.")]);

        var sessionId = new SessionId("console/subagent-tool-isolation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-tool-isolation-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);

        foreach (var content in new[] { "Run the first child.", "Run the second child." })
        {
            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = content,
                Source = BuildPersonalSource()
            }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await ExpectTurnCompletedAsync(
                subscriber,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }

        Assert.True(_clientProvider.Compaction.ReceivedToolNames.Count >= 3);
        Assert.Equal(
            ["file_read", "load_tool", "search_tools"],
            _clientProvider.Compaction.ReceivedToolNames[0]
                .OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(
            ["file_read", "load_tool", "search_tools", "synthetic_catalog__tool_007"],
            _clientProvider.Compaction.ReceivedToolNames[1]
                .OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(
            ["file_read", "load_tool", "search_tools"],
            _clientProvider.Compaction.ReceivedToolNames[2]
                .OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Child_native_correction_exposes_one_tool_only_until_child_completion()
    {
        const string deferredToolName = ApprovalProbeToolName;
        var shellProbe = new RecordingContextTool("shell_execute", "shell must not run", "shell");
        _toolRegistry.Register(shellProbe);

        _clientProvider.Main.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-native-child-spawn-1",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Inspect with the native probe."
                })
        ]);
        _clientProvider.Main.PlannedResponses.Enqueue([new TextContent("First child returned.")]);
        _clientProvider.Main.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-native-child-spawn-2",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Start without inherited tools."
                })
        ]);
        _clientProvider.Main.PlannedResponses.Enqueue([new TextContent("Second child returned.")]);
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-native-child-correction",
                "shell_execute",
                new Dictionary<string, object?>
                {
                    ["command"] = $"{deferredToolName} --inspect"
                })
        ]);
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-native-child-authorization",
                deferredToolName,
                new Dictionary<string, object?>())
        ]);
        _clientProvider.Compaction.PlannedResponses.Enqueue([new TextContent("First child completed.")]);
        _clientProvider.Compaction.PlannedResponses.Enqueue([new TextContent("Fresh child completed.")]);

        var sessionId = new SessionId("console/subagent-native-correction-isolation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-native-correction-events");
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);

        var source = BuildPersonalSource();
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run the correcting child.",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SubAgentOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        var approval = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(deferredToolName, approval.ToolName.Value);
        var denied = await sessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = approval.CallId,
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.Deny),
            SenderId = source.SenderId!
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(denied);
        await ExpectTurnCompletedAsync(
            subscriber,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run a fresh child.",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await ExpectTurnCompletedAsync(
            subscriber,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(_clientProvider.Compaction.ReceivedToolNames.Count >= 4);
        Assert.DoesNotContain(deferredToolName, _clientProvider.Compaction.ReceivedToolNames[0]);
        Assert.Contains(deferredToolName, _clientProvider.Compaction.ReceivedToolNames[1]);
        Assert.Contains(deferredToolName, _clientProvider.Compaction.ReceivedToolNames[2]);
        Assert.DoesNotContain(deferredToolName, _clientProvider.Compaction.ReceivedToolNames[^1]);
        Assert.False(shellProbe.WasCalled);
        Assert.NotNull(_recordingApprovalTool);
        Assert.False(_recordingApprovalTool!.WasCalled);
        var correction = GetToolResult(
            _clientProvider.Compaction.ReceivedMessages[1],
            "call-native-child-correction");
        Assert.Equal(
            $"Shell execution stopped because '{deferredToolName}' is a native Netclaw tool.\n" +
            "Next action: call the native Netclaw tool named in this result directly instead of shell_execute.",
            correction);
    }

    [Fact]
    public async Task Child_model_failure_discards_loaded_tools_before_a_fresh_child()
    {
        const string failureProbeName = "failure_probe";
        var failureProbe = new RecordingContextTool(
            failureProbeName,
            "probe complete",
            onExecute: _ => _clientProvider.Compaction.PlannedExceptions.Enqueue(
                new InvalidOperationException("synthetic child model failure")));
        _toolRegistry.Register(failureProbe);

        _clientProvider.Main.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-failure-spawn-1",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Load the failure probe."
                })
        ]);
        _clientProvider.Main.PlannedResponses.Enqueue([new TextContent("Failure was recorded.")]);
        _clientProvider.Main.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-failure-spawn-2",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Start with a clean tool set."
                })
        ]);
        _clientProvider.Main.PlannedResponses.Enqueue([new TextContent("Fresh child completed.")]);
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-failure-load",
                "load_tool",
                new Dictionary<string, object?> { ["Name"] = failureProbeName })
        ]);
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-failure-execute",
                failureProbeName,
                new Dictionary<string, object?>())
        ]);
        _clientProvider.Compaction.PlannedResponses.Enqueue([new TextContent("Fresh child completed.")]);

        var sessionId = new SessionId("console/subagent-failure-isolation");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("subagent-failure-isolation-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);

        foreach (var content in new[] { "Run the failing child.", "Run the fresh child." })
        {
            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = content,
                Source = BuildPersonalSource()
            }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await ExpectTurnCompletedAsync(
                subscriber,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }

        Assert.True(failureProbe.WasCalled);
        Assert.Contains(
            "synthetic child model failure",
            GetToolResult(
                _clientProvider.Main.ReceivedMessages[1],
                "call-failure-spawn-1"),
            StringComparison.Ordinal);
        Assert.Equal(
            ["file_read", "load_tool", "search_tools"],
            _clientProvider.Compaction.ReceivedToolNames[0]
                .OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Contains(failureProbeName, _clientProvider.Compaction.ReceivedToolNames[1]);
        Assert.Equal(
            ["file_read", "load_tool", "search_tools"],
            _clientProvider.Compaction.ReceivedToolNames[2]
                .OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Spawn_agent_subagent_approval_uses_parent_authority_and_resumes_after_approval()
    {
        const string parentCallId = "call_5aaea0c7afec4e47bbc062d8";
        const string childCallId = "call_6f11cdf0c19746c59e778331";

        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            CreateToolCall(
                parentCallId,
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "approval-tester",
                    ["task"] = "Run the approval probe"
                })
        ];
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                "call-load-approval-probe",
                "load_tool",
                new Dictionary<string, object?>
                {
                    ["Name"] = ApprovalProbeToolName
                })
        ]);
        _clientProvider.Compaction.PlannedResponses.Enqueue(
        [
            CreateToolCall(
                childCallId,
                ApprovalProbeToolName,
                new Dictionary<string, object?>
                {
                    // Per-call timeout hint on the sub-agent path: the sub-agent
                    // loop must extract this via the shared executor seam and apply
                    // it to the tool context (it previously skipped extraction and
                    // silently dropped the hint).
                    ["_timeout_seconds"] = 1800
                })
        ]);

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
            Content = "Use a subagent to run the approval probe",
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
        Assert.Equal(ApprovalProbeToolName, request.ToolName.Value);
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

        Assert.NotNull(_recordingApprovalTool);
        Assert.True(_recordingApprovalTool!.WasCalled);
        Assert.Equal(TrustAudience.Personal, _recordingApprovalTool.LastContext?.Audience);
        Assert.Contains(
            ApprovalProbeToolName,
            _clientProvider.Compaction.ReceivedToolNames[1]);

        // The sub-agent extracted the meta timeout hint and applied it to the
        // tool context (regression guard for the previously-dropped hint).
        Assert.Equal(TimeSpan.FromSeconds(1800), _recordingApprovalTool.LastContext?.ExecutionTimeout.Value);
    }

    [Fact]
    public async Task Spawn_agent_subagent_approval_expires_after_parent_session_recovery()
    {
        _clientProvider.Main.ToolCallsOnFirstCall =
        [
            CreateToolCall(
                "call-spawn-approval-expire",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "approval-tester",
                    ["task"] = "Run the approval probe"
                })
        ];
        _clientProvider.Compaction.ToolCallsOnFirstCall =
        [
            CreateToolCall(
                "call-subagent-approval-expire",
                ApprovalProbeToolName,
                new Dictionary<string, object?>())
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
            Content = "Use a subagent to run the approval probe",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("subagent-approval", request.CallId.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("call-subagent-approval-expire", request.CallId.Value, StringComparison.Ordinal);
        AssertApprovalButtonValuesRoundTrip(request);
        Assert.False(_recordingApprovalTool!.WasCalled);

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
        Assert.False(_recordingApprovalTool.WasCalled);

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
                result.CallId == "call-spawn-approval-expire"
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
        var routedTask = Assert.Single(
            subagentCall,
            static message => message.Role == Microsoft.Extensions.AI.ChatRole.User);
        Assert.Contains("Context:\n[working-context]", routedTask.Text, StringComparison.Ordinal);
        Assert.Contains("platform: Linux", routedTask.Text, StringComparison.Ordinal);
        Assert.Contains("executable: /bin/bash", routedTask.Text, StringComparison.Ordinal);
        Assert.EndsWith("Task:\ncheck daemon health", routedTask.Text, StringComparison.Ordinal);
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
            && m.Text.Contains("[session]\nsession_dir:", StringComparison.Ordinal)
            && m.Text.EndsWith("Task:\ncheck scheduled health", StringComparison.Ordinal));
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
            CreateToolCall(
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

    private void RegisterSyntheticMcpCatalog()
    {
        for (var i = 0; i < SyntheticMcpToolCount; i++)
        {
            var toolName = $"tool_{i:D3}";
            var function = AIFunctionFactory.Create(
                (string query, int maxResults) => $"unused:{query}:{maxResults}",
                toolName,
                "Find synthetic records by query with a bounded result count.");
            _toolRegistry.Register(new McpToolAdapter(
                function,
                SyntheticMcpServerName,
                toolName));
        }
    }

    private static async Task<ToolFootprintBaseline> ReadToolFootprintBaselineAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "ToolFootprintEvidence",
            "tool-schema-footprint-baseline.json");
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        return JsonSerializer.Deserialize<ToolFootprintBaseline>(json, options)
               ?? throw new InvalidOperationException("Tool footprint baseline is empty.");
    }

    private static string GetToolResult(IReadOnlyList<ChatMessage> messages, string callId)
    {
        var result = messages
            .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Single(content => content.CallId == callId)
            .Result;
        return Assert.IsType<string>(result);
    }

    private sealed record ToolFootprintBaseline(
        int SchemaVersion,
        string Scenario,
        string Metric,
        int SyntheticMcpToolCount,
        ModelVisibleToolFootprint MainCore,
        ModelVisibleToolFootprint SubagentFull);

    private sealed class RecordingRoleChatClientProvider : IChatClientProvider
    {
        public FakeChatClient Main { get; } = new();
        public FakeChatClient Compaction { get; } = new();

        public IChatClient GetClient(ModelRole role)
            => role == ModelRole.Compaction ? Compaction : Main;
    }

    private sealed class RecordingContextTool(
        string name,
        string result,
        string grantCategory = "builtin",
        Action<ToolInvocationContext>? onExecute = null) : INetclawTool
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
            onExecute?.Invoke(context);
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
