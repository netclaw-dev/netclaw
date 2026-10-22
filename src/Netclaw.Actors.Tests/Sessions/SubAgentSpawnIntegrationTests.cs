// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Journal;
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
using AkkaPersistence = Akka.Persistence.Persistence;
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

        var skillRoot = Path.Join(TestPaths.BasePath, "skill-routing");
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
        var subAgentPaths = TestPaths;
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
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SubAgentSpawner>.Instance, new SessionConfig());

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
    public async Task Spawn_agent_returns_durable_acceptance_then_wakes_parent_with_a_result_notice()
    {
        var childGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentWakeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientProvider.Compaction.NextResponseGate = childGate;
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

        try
        {
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
        await _clientProvider.Compaction.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var acceptance = await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("call-spawn", acceptance.CallId.Value);
        Assert.Equal("spawn_agent", acceptance.ToolName.Value);
        string runId;
        using (var acceptedJson = JsonDocument.Parse(acceptance.Result))
        {
            Assert.Equal("accepted", acceptedJson.RootElement.GetProperty("state").GetString());
            Assert.Equal("check_agent_run", acceptedJson.RootElement.GetProperty("control_tool").GetString());
            runId = acceptedJson.RootElement.GetProperty("run_id").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(runId));
        }

        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", text.Text, StringComparison.OrdinalIgnoreCase);

        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        // The child model stays behind a barrier until the parent returns its acceptance.
        Assert.Equal(1, _clientProvider.Compaction.CallCount);
        Assert.Equal(2, _clientProvider.Main.CallCount);
        _clientProvider.Main.NextResponseGate = parentWakeGate;
        childGate.TrySetResult();

        var completed = await subscriber.ExpectMsgAsync<SubAgentOutput>(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.Equal("summarizer", completed.AgentName.Value);
        Assert.True(completed.Success);
        Assert.Equal(SubAgentRunOutcome.Completed, completed.Outcome);
        Assert.True(completed.TimestampMs >= started.TimestampMs);
        Assert.True(completed.Duration >= TimeSpan.FromMilliseconds(completed.TimestampMs - started.TimestampMs));
        Assert.Equal(0, completed.FindingsCount);

        await AwaitAssertAsync(() => Assert.True(_clientProvider.Main.CallCount >= 3,
            $"Expected a parent wake with the child result notice, saw {_clientProvider.Main.CallCount} calls."),
            cancellationToken: TestContext.Current.CancellationToken);
        var parentWake = _clientProvider.Main.ReceivedMessages[2];
        Assert.DoesNotContain(parentWake.SelectMany(message => message.Contents.OfType<FunctionCallContent>()),
            call => call.Name == "child_run_result");
        var notice = Assert.Single(NoticeTexts(parentWake), text => text.Contains(runId, StringComparison.Ordinal));
        Assert.Contains("agent=summarizer", notice, StringComparison.Ordinal);
        Assert.Contains("outcome=completed", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("[fake]", notice, StringComparison.Ordinal);
        var resultText = ReadNoticeFile(notice);
        Assert.Contains(runId, resultText, StringComparison.Ordinal);
        Assert.Contains("[fake]", resultText, StringComparison.Ordinal);
        Assert.Single(parentWake, message => message.Role == Microsoft.Extensions.AI.ChatRole.User
            && message.Text == "Use a subagent to summarize the file");
        parentWakeGate.TrySetResult();
        var finalText = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("fake", finalText.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

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
        finally
        {
            childGate.TrySetResult();
            parentWakeGate.TrySetResult();
        }
    }

    [Fact]
    public async Task Child_result_delivered_in_the_middle_of_a_refused_loop_does_not_reset_the_refused_repeat_count()
    {
        // S1 of the re-review: a child result that the session delivers in the middle of the tool loop clears the
        // recurrence evidence, so the refused call runs two more times. It must not clear the count. Otherwise
        // the model could start a child next to each refused repeat and never reach the quiet end.
        var childGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientProvider.Compaction.NextResponseGate = childGate;
        List<AIContent> Search(int round)
            => [CreateToolCall($"search-{round}", "search_tools", new Dictionary<string, object?> { ["query"] = "browser" })];
        // Request 1 starts the child. Requests 2 and 3 run. Request 4 is the first correction. Requests 5 to 7 are
        // refused repeats 1 to 3. Request 7 waits at the parent gate. The child ends, and the runtime writes
        // result.md off the actor thread. The test opens the parent gate only after the journal holds the admission
        // of the notice, so the notice joins the loop at the next tool boundary: request 8. The refused call then runs
        // again, the next request is the correction, and the later requests are repeats. The count keeps its value
        // across the notice, so the loop reaches repeat 5. The model answers the final nudge in text.
        var request = 0;
        _clientProvider.Main.ResponseContentsSelector = messages =>
        {
            var number = Interlocked.Increment(ref request);
            // The next request waits at the gate, so the child can finish while the parent loop runs.
            if (number == 6)
                _clientProvider.Main.NextResponseGate = parentGate;
            if (number == 1)
                return [CreateToolCall("call-spawn", "spawn_agent", new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Summarize src/README.md"
                })];
            return messages[^1].Text.Contains("Stop trying this action.", StringComparison.Ordinal)
                ? [new TextContent("The search stays blocked.")]
                : Search(number);
        };

        try
        {
            var sessionId = new SessionId("console/child-mid-loop-count");
            var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
            var subscriber = CreateTestProbe("child-mid-loop-events");
            await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
            {
                SessionId = sessionId,
                Filter = OutputFilter.Full
            }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Start a helper and repeat the search.",
                Source = BuildPersonalSource()
            }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            var outputs = new List<object>();
            await AwaitAssertAsync(
                () => Assert.True(_clientProvider.Main.CallCount >= 7, $"Request 7 is not in flight yet ({_clientProvider.Main.CallCount})."),
                TimeSpan.FromSeconds(20), cancellationToken: TestContext.Current.CancellationToken);
            childGate.TrySetResult();
            await subscriber.FishForMessageAsync<object>(message =>
            {
                outputs.Add(message);
                return message is SubAgentOutput { Phase: SubAgentPhase.Completed };
            }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            // The Completed output comes before result.md and before the admission of the notice. A slow runner
            // takes several loop rounds for that work, so the test waits for the durable admission. The session
            // buffers the notice in the persist callback, and the callback runs before the actor reads the reply
            // to request 7. Thus the notice is in the buffer before the loop reaches the next tool boundary.
            var journalProbe = CreateTestProbe("child-mid-loop-journal");
            await AwaitAssertAsync(async () => Assert.True(
                    await HasChildNoticeAdmissionAsync(journalProbe, sessionId),
                    "The journal holds no admission of the child notice yet."),
                TimeSpan.FromSeconds(20), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(7, _clientProvider.Main.CallCount);
            parentGate.TrySetResult();
            await subscriber.FishForMessageAsync<object>(message =>
            {
                outputs.Add(message);
                return message is TurnCompleted;
            }, TimeSpan.FromSeconds(20), cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain(outputs, static output => output is ErrorOutput);
            var requests = _clientProvider.Main.ReceivedMessages;
            // The notice is in the history from request N on, and the evidence is clear: request N runs.
            var noticeAt = requests.ToList().FindIndex(static request => request.Any(static message =>
                message.Role == Microsoft.Extensions.AI.ChatRole.User
                && message.Text.StartsWith("Child run ended.", StringComparison.Ordinal)));
            Assert.Equal(7, noticeAt);
            static bool IsRepeatNudge(Microsoft.Extensions.AI.ChatMessage message)
                => message.Role == Microsoft.Extensions.AI.ChatRole.User
                    && message.Text.StartsWith("[system: Netclaw refused your repeated call to `search_tools` again.",
                        StringComparison.Ordinal);
            // The count does not reset at the notice. Repeat 5 gets the final nudge, and the model answers it. The
            // notice in request 8 comes before repeat 4, so repeats 4 and 5 follow it. A reset count would give five
            // nudges after the notice.
            // Review Should-fix 4: count every nudge of the turn, before and after the notice. The history keeps only
            // the latest nudge, so a request sends a nudge when the nudge comes after its last assistant message. The
            // notice can follow the nudge in that tail, so the check does not read only the last message.
            static string? FreshNudge(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> request)
            {
                var lastAssistant = -1;
                for (var index = 0; index < request.Count; index++)
                {
                    if (request[index].Role == Microsoft.Extensions.AI.ChatRole.Assistant)
                        lastAssistant = index;
                }

                return request.Skip(lastAssistant + 1).FirstOrDefault(IsRepeatNudge)?.Text;
            }

            var nudges = Enumerable.Range(0, requests.Count)
                .Where(index => FreshNudge(requests[index]) is not null)
                .ToArray();
            // A reset at the notice gives more than five nudges. A first repeat after the notice that is final gives
            // fewer than five nudges.
            Assert.True(nudges.Length == 5,
                $"Expected exactly 5 repeat nudges in the turn, got {nudges.Length} "
                + $"(requests {string.Join(", ", nudges.Select(static index => index + 1))}; notice in request {noticeAt + 1}).");
            Assert.True(nudges[0] < noticeAt && noticeAt < nudges[4],
                $"The count must span the notice (nudges in requests {string.Join(", ", nudges.Select(static index => index + 1))}, "
                + $"notice in request {noticeAt + 1}).");
            Assert.All(nudges[..4], index =>
                Assert.DoesNotContain("Stop trying this action.", FreshNudge(requests[index]), StringComparison.Ordinal));
            Assert.Contains("Stop trying this action.", FreshNudge(requests[nudges[4]]), StringComparison.Ordinal);
            Assert.Equal(nudges[4] + 1, _clientProvider.Main.CallCount);
        }
        finally
        {
            childGate.TrySetResult();
            parentGate.TrySetResult();
        }
    }

    // True when the in-memory journal holds the admission of a child notice for the session.
    private static async Task<bool> HasChildNoticeAdmissionAsync(Akka.TestKit.TestProbe probe, SessionId sessionId)
    {
        AkkaPersistence.Instance.Apply(probe.Sys).JournalFor(null).Tell(
            new MemoryJournal.ReplayAllEvents(0, int.MaxValue, int.MaxValue, probe.Ref));
        var found = false;
        while (true)
        {
            switch (await probe.ExpectMsgAsync<object>(TimeSpan.FromSeconds(5),
                        cancellationToken: TestContext.Current.CancellationToken))
            {
                case MemoryJournal.ReplayedEvent { Persistent.Payload: InputAdmitted { ChildNotice: not null } admitted }
                    when admitted.SessionId == sessionId:
                    found = true;
                    break;
                case MemoryJournal.EventReplaySuccess:
                    return found;
                case MemoryJournal.EventReplayFailure failure:
                    throw new InvalidOperationException("The journal replay failed.", failure.Cause);
            }
        }
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

        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use the summarizer.",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);

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

        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use one deferred child tool.",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);

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

        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Use a child to check unavailable tools.",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);

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
        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();
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
            var approval = await subscriber.FishForMessageAsync<ToolInteractionRequest>(
                request => request.ToolName.Value == ShellTool.ToolName,
                TimeSpan.FromSeconds(15),
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
        await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);

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
        (string Prompt, FunctionCallContent Spawn)[] parentSpawns =
        [
            ("Run the first child.", CreateToolCall(
                "call-isolation-spawn-1",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Use one synthetic tool."
                })),
            ("Run the second child.", CreateToolCall(
                "call-isolation-spawn-2",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Start without inherited tools."
                }))
        ];
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

        var completedChildren = 0;
        foreach (var (content, _) in parentSpawns)
        {
            var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
            var parentResumeMarker = PrepareParentSpawnReplies(parentSpawns);
            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = content,
                Source = BuildPersonalSource()
            }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await ExpectChildTerminalAndParentResumeAsync(
                subscriber,
                "summarizer",
                TestContext.Current.CancellationToken,
                parentCallCountBeforeSend,
                parentResumeMarker,
                ++completedChildren);
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

        (string Prompt, FunctionCallContent Spawn)[] parentSpawns =
        [
            ("Run the correcting child.", CreateToolCall(
                "call-native-child-spawn-1",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Inspect with the native probe."
                })),
            ("Run a fresh child.", CreateToolCall(
                "call-native-child-spawn-2",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Start without inherited tools."
                }))
        ];
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
        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentSpawnReplies(parentSpawns);
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = parentSpawns[0].Prompt,
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SubAgentOutput>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        var approval = await subscriber.FishForMessageAsync<ToolInteractionRequest>(
            request => request.ToolName.Value == deferredToolName,
            TimeSpan.FromSeconds(15),
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
        await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker,
            1);

        parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        parentResumeMarker = PrepareParentSpawnReplies(parentSpawns);
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = parentSpawns[1].Prompt,
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker,
            2);

        Assert.True(_clientProvider.Compaction.ReceivedToolNames.Count >= 4);
        Assert.DoesNotContain(deferredToolName, _clientProvider.Compaction.ReceivedToolNames[0]);
        Assert.Contains(deferredToolName, _clientProvider.Compaction.ReceivedToolNames[1]);
        // The call to the deferred tool parks, and a new incarnation resumes after the answer. A park does not keep
        // the loaded deferred tools (durable-child-consent-wait, PR 1 cut plan), so the resumed call does not show it.
        Assert.DoesNotContain(deferredToolName, _clientProvider.Compaction.ReceivedToolNames[2]);
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

        (string Prompt, FunctionCallContent Spawn)[] parentSpawns =
        [
            ("Run the failing child.", CreateToolCall(
                "call-failure-spawn-1",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Load the failure probe."
                })),
            ("Run the fresh child.", CreateToolCall(
                "call-failure-spawn-2",
                "spawn_agent",
                new Dictionary<string, object?>
                {
                    ["agent"] = "summarizer",
                    ["task"] = "Start with a clean tool set."
                }))
        ];
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

        var completedChildren = 0;
        foreach (var (content, _) in parentSpawns)
        {
            var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
            var parentResumeMarker = PrepareParentSpawnReplies(parentSpawns);
            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = content,
                Source = BuildPersonalSource()
            }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            var (terminal, _) = await ExpectChildTerminalAndParentResumeWithOutputsAsync(
                subscriber,
                "summarizer",
                TestContext.Current.CancellationToken,
                parentCallCountBeforeSend,
                parentResumeMarker,
                ++completedChildren);
            Assert.Equal(completedChildren == 2, terminal.Success);
            Assert.Equal(
                completedChildren == 2 ? SubAgentRunOutcome.Completed : SubAgentRunOutcome.Failed,
                terminal.Outcome);
            if (completedChildren == 1)
                Assert.Equal(SubAgentOutcomeReason.LlmCallFailed, terminal.OutcomeReason);
        }

        Assert.True(failureProbe.WasCalled);
        var spawnReceiptRequest = _clientProvider.Main.ReceivedMessages
            .LastOrDefault(request => request.SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
                .Any(result => result.CallId == "call-failure-spawn-1"));
        Assert.NotNull(spawnReceiptRequest);
        var firstRunId = ParseAcceptedRunId(GetToolResult(spawnReceiptRequest!, "call-failure-spawn-1"));
        Assert.False(string.IsNullOrWhiteSpace(firstRunId));
        var failedChildResultRequest = _clientProvider.Main.ReceivedMessages
            .LastOrDefault(request => FindDeliveredRunIds(request).Contains(firstRunId!));
        Assert.NotNull(failedChildResultRequest);
        Assert.Contains(
            "synthetic child model failure",
            GetChildRunResult(failedChildResultRequest!, firstRunId!),
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

        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();
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

        ToolResultOutput? accepted = null;
        ToolInteractionRequest? request = null;
        // The child approval and parent acceptance receipt can arrive in either order.
        await subscriber.FishForMessageAsync<SessionOutput>(
            output =>
            {
                if (output is ToolResultOutput result && result.CallId.Value == parentCallId)
                    accepted = result;
                if (output is ToolInteractionRequest interaction && interaction.ToolName.Value == ApprovalProbeToolName)
                    request = interaction;
                return accepted is not null && request is not null;
            },
            TimeSpan.FromSeconds(15),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("spawn_agent", accepted!.ToolName.Value);
        string runId;
        using (var acceptedJson = JsonDocument.Parse(accepted.Result))
        {
            Assert.Equal("accepted", acceptedJson.RootElement.GetProperty("state").GetString());
            runId = acceptedJson.RootElement.GetProperty("run_id").GetString()!;
        }

        Assert.NotEqual(childCallId, request!.CallId.Value);
        Assert.StartsWith($"{runId}/subagent-approval/", request.CallId.Value, StringComparison.Ordinal);
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

        var completed = await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "approval-tester",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.True(completed.Success);

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
    // PR 2 of child-approval-events-and-parent-pause (design D6): recovery withdraws the open prompt of a child that
    // was live at the restart, and a later click gets the restart text. The text names no run and no agent. The nack is
    // not the "expired" nack, so the Mattermost synchronous reply does not say "expired" either.
    public async Task Spawn_agent_subagent_approval_is_withdrawn_after_parent_session_recovery()
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

        ToolResultOutput? acceptance = null;
        ToolInteractionRequest? request = null;
        for (var i = 0; i < 12 && (acceptance is null || request is null); i++)
        {
            var output = await subscriber.ExpectMsgAsync<SessionOutput>(
                TimeSpan.FromSeconds(15),
                cancellationToken: TestContext.Current.CancellationToken);
            if (output is ToolResultOutput result && result.CallId.Value == "call-spawn-approval-expire")
                acceptance = result;
            if (output is ToolInteractionRequest interaction && interaction.ToolName.Value == ApprovalProbeToolName)
                request = interaction;
        }

        Assert.NotNull(acceptance);
        using (var acceptedJson = JsonDocument.Parse(acceptance!.Result))
            Assert.Equal("accepted", acceptedJson.RootElement.GetProperty("state").GetString());
        Assert.NotNull(request);
        string runId;
        using (var acceptedJson = JsonDocument.Parse(acceptance.Result))
            runId = acceptedJson.RootElement.GetProperty("run_id").GetString()!;
        Assert.StartsWith($"{runId}/subagent-approval/", request.CallId.Value, StringComparison.Ordinal);
        Assert.Contains("subagent-approval", request.CallId.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("call-subagent-approval-expire", request.CallId.Value, StringComparison.Ordinal);
        AssertApprovalButtonValuesRoundTrip(request);
        Assert.False(_recordingApprovalTool!.WasCalled);

        await ColdRespawnAsync(sessionId);

        var recoveredCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientProvider.Main.NextResponseGate = recoveredCall;
        try
        {
            var subscriberB = CreateTestProbe("subagent-approval-expired-events-b");
            await sessionManager.Ask<SessionJoined>(new JoinSession(subscriberB)
            {
                SessionId = sessionId,
                Filter = OutputFilter.Full
            }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await subscriberB.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

            await AwaitAssertAsync(() =>
            {
                Assert.Null(_clientProvider.Main.NextResponseGate);
                Assert.Contains(_clientProvider.Main.ReceivedMessages, messages => FindDeliveredRunIds(messages).Contains(runId));
            }, TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);

            var reply = await sessionManager.Ask<ISessionResponse>(new ToolInteractionResponse
            {
                SessionId = sessionId,
                CallId = request.CallId,
                SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
                SenderId = source.SenderId!
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var withdrawn = await subscriberB.FishForMessageAsync<TextOutput>(
                output => ChildWithdrawalTexts.IsApprovalReply(output.Text),
                TimeSpan.FromSeconds(15),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ChildWithdrawalTexts.For(WithdrawReason.DaemonRestarted), withdrawn.Text);
            var nack = Assert.IsType<CommandNack>(reply);
            Assert.NotEqual(ApprovalNackReasons.PromptExpired, nack.Reason);
            Assert.DoesNotContain(runId, withdrawn.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("approval-tester", withdrawn.Text, StringComparison.Ordinal);
            Assert.False(_recordingApprovalTool.WasCalled);

            await AwaitAssertAsync(() =>
            {
                var resumedRequest = _clientProvider.Main.ReceivedMessages
                    .LastOrDefault(messages => FindDeliveredRunIds(messages).Contains(runId));
                Assert.NotNull(resumedRequest);
                var resumedResult = GetChildRunResult(resumedRequest!, runId);
                Assert.Contains("did not survive", resumedResult, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("reason: owner_restarted", resumedResult, StringComparison.Ordinal);
                Assert.Contains("uncertain", resumedResult, StringComparison.OrdinalIgnoreCase);

                var originalReceipt = Assert.Single(resumedRequest
                    .SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
                    result => result.CallId == "call-spawn-approval-expire");
                using (var originalJson = JsonDocument.Parse(originalReceipt.Result?.ToString()!))
                    Assert.Equal("accepted", originalJson.RootElement.GetProperty("state").GetString());

                var spawnCalls = _clientProvider.Main.ReceivedMessages
                    .SelectMany(static callMessages => callMessages)
                    .SelectMany(static message => message.Contents.OfType<FunctionCallContent>())
                    .Where(static call => call.Name == "spawn_agent")
                    .Select(static call => call.CallId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                Assert.Equal(1, spawnCalls);
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, _clientProvider.Compaction.CallCount);
            Assert.False(_recordingApprovalTool.WasCalled);
        }
        finally
        {
            recoveredCall.TrySetResult();
        }
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
        // Arrange
        var sessionId = new SessionId("test-channel/routed-slash-success");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-success-events");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();

        // Act
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check daemon health",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var completed = await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);

        // Assert
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.Equal("summarizer", completed.AgentName.Value);
        Assert.True(completed.Success);

        Assert.Equal(parentCallCountBeforeSend + 1, _clientProvider.Main.CallCount);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);
        var resumedRequest = _clientProvider.Main.ReceivedMessages
            .Skip(parentCallCountBeforeSend)
            .Last(request => CountDeliveredChildRuns(request) == 1);
        var acceptedRunId = Assert.Single(FindAcceptedRunIds(resumedRequest));
        Assert.NotEmpty(GetChildRunResult(resumedRequest, acceptedRunId));

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

    [Fact]
    public async Task Routed_slash_command_reports_child_model_failure_and_completes_the_turn()
    {
        // Arrange
        const string failure = "synthetic routed model failure";
        _clientProvider.Compaction.PlannedExceptions.Enqueue(new InvalidOperationException(failure));
        var sessionId = new SessionId("test-channel/routed-slash-child-failure");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("routed-slash-child-failure-events");
        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check daemon health",
            Source = BuildPersonalSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var completed = await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);

        // Assert
        Assert.Equal(parentCallCountBeforeSend + 1, _clientProvider.Main.CallCount);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.False(completed.Success);
        Assert.Equal(SubAgentRunOutcome.Failed, completed.Outcome);
        Assert.Equal(SubAgentOutcomeReason.LlmCallFailed, completed.OutcomeReason);
        var resumedRequest = _clientProvider.Main.ReceivedMessages
            .Skip(parentCallCountBeforeSend)
            .Last(request => CountDeliveredChildRuns(request) == 1);
        var acceptedRunId = Assert.Single(FindAcceptedRunIds(resumedRequest));
        Assert.Contains(failure, GetChildRunResult(resumedRequest, acceptedRunId), StringComparison.Ordinal);
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

        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check scheduled health",
            Source = BuildReminderSource()
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var (completed, outputs) = await ExpectChildTerminalAndParentResumeWithOutputsAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);

        Assert.Equal(parentCallCountBeforeSend + 1, _clientProvider.Main.CallCount);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.Equal(1, _clientProvider.Compaction.CallCount);
        AssertAcceptedSpawnReceipt(outputs.OfType<TextOutput>().First().Text);

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
        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();

        var firstAck = await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route check scheduled health",
            Source = reminderSource
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, firstAck.SessionId);

        var (completed, outputs) = await ExpectChildTerminalAndParentResumeWithOutputsAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);
        Assert.Equal(parentCallCountBeforeSend + 1, _clientProvider.Main.CallCount);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        AssertAcceptedSpawnReceipt(outputs.OfType<TextOutput>().First().Text);

        var callsAfterFirst = _clientProvider.Compaction.CallCount;
        var parentCallsAfterFirst = _clientProvider.Main.CallCount;

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
            Assert.Equal(parentCallsAfterFirst, _clientProvider.Main.CallCount);
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
        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();

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

        var completed = await ExpectChildTerminalAndParentResumeAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);
        Assert.Equal(parentCallCountBeforeSend + 1, _clientProvider.Main.CallCount);

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
        var parentCallCountBeforeSend = _clientProvider.Main.CallCount;
        var parentResumeMarker = PrepareParentResumeMarker();

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "/ops-route-restrictive run health check",
            Source = source
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var (completed, outputs) = await ExpectChildTerminalAndParentResumeWithOutputsAsync(
            subscriber,
            "summarizer",
            TestContext.Current.CancellationToken,
            parentCallCountBeforeSend,
            parentResumeMarker);
        Assert.Equal(parentCallCountBeforeSend + 1, _clientProvider.Main.CallCount);
        Assert.Equal(SubAgentPhase.Completed, completed.Phase);

        AssertAcceptedSpawnReceipt(outputs.OfType<TextOutput>().First().Text);
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

    private static void AssertAcceptedSpawnReceipt(string text)
    {
        using var receipt = JsonDocument.Parse(text);
        Assert.Equal("accepted", receipt.RootElement.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(receipt.RootElement.GetProperty("run_id").GetString()));
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

    private static async Task<IReadOnlyList<SessionOutput>> CollectTurnOutputsAsync(
        Akka.TestKit.TestProbe probe, CancellationToken ct)
    {
        var outputs = new List<SessionOutput>();
        for (var i = 0; i < 12; i++)
        {
            var output = await probe.ExpectMsgAsync<SessionOutput>(TimeSpan.FromSeconds(5), cancellationToken: ct);
            outputs.Add(output);
            if (output is TurnCompleted)
                return outputs;
        }

        throw new Xunit.Sdk.XunitException("Expected TurnCompleted within 12 session outputs.");
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

    private static async Task<SubAgentOutput> ExpectChildTerminalAsync(
        Akka.TestKit.TestProbe probe,
        string agentName,
        CancellationToken ct)
    {
        var terminal = await probe.FishForMessageAsync<SubAgentOutput>(
            output => output.Phase == SubAgentPhase.Completed && output.AgentName.Value == agentName,
            TimeSpan.FromSeconds(15),
            cancellationToken: ct);
        Assert.Equal(SubAgentPhase.Completed, terminal.Phase);
        Assert.Equal(agentName, terminal.AgentName.Value);
        return terminal;
    }

    private async Task<SubAgentOutput> ExpectChildTerminalAndParentResumeAsync(
        Akka.TestKit.TestProbe probe,
        string agentName,
        CancellationToken ct,
        int parentCallCountBeforeSend,
        string parentResumeMarker,
        int expectedChildResults = 1)
    {
        var result = await ExpectChildTerminalAndParentResumeWithOutputsAsync(
            probe,
            agentName,
            ct,
            parentCallCountBeforeSend,
            parentResumeMarker,
            expectedChildResults);
        return result.Terminal;
    }

    private async Task<(SubAgentOutput Terminal, IReadOnlyList<SessionOutput> Outputs)> ExpectChildTerminalAndParentResumeWithOutputsAsync(
        Akka.TestKit.TestProbe probe,
        string agentName,
        CancellationToken ct,
        int parentCallCountBeforeSend,
        string parentResumeMarker,
        int expectedChildResults = 1)
    {
        var outputs = new List<SessionOutput>();
        SubAgentOutput? terminal = null;
        var markerSeen = false;
        var resumedTurnCompleted = false;
        while (terminal is null || !resumedTurnCompleted)
        {
            var output = await probe.ExpectMsgAsync<SessionOutput>(
                TimeSpan.FromSeconds(15),
                cancellationToken: ct);
            outputs.Add(output);
            if (output is SubAgentOutput child
                && child.Phase == SubAgentPhase.Completed
                && child.AgentName.Value == agentName)
            {
                terminal = child;
            }

            if (output is TextOutput text && text.Text == parentResumeMarker)
                markerSeen = true;
            else if (markerSeen && output is TurnCompleted)
                resumedTurnCompleted = true;
        }

        Assert.NotNull(terminal);
        await AwaitAssertAsync(() =>
        {
            var latestParentRequest = _clientProvider.Main.ReceivedMessages
                .Skip(parentCallCountBeforeSend)
                .LastOrDefault(request => CountDeliveredChildRuns(request) >= expectedChildResults);
            Assert.True(
                latestParentRequest is not null,
                $"Expected a new parent request with {expectedChildResults} distinct child result deliveries after call {parentCallCountBeforeSend}.");
            Assert.Equal(expectedChildResults, CountDeliveredChildRuns(latestParentRequest!));
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100), cancellationToken: ct);

        return (terminal!, outputs);
    }

    private string PrepareParentResumeMarker()
    {
        var marker = $"parent-resumed-{Guid.NewGuid():N}";
        var priorRunIds = _clientProvider.Main.ReceivedMessages
            .SelectMany(FindAcceptedRunIds)
            .ToHashSet(StringComparer.Ordinal);
        _clientProvider.Main.ResponseContentsSelector = request =>
        {
            var newRunIds = FindAcceptedRunIds(request)
                .Except(priorRunIds, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            var deliveredRunIds = FindDeliveredRunIds(request);
            return newRunIds.Count == 1 && newRunIds.IsSubsetOf(deliveredRunIds)
                ? [new TextContent(marker)]
                : null;
        };
        return marker;
    }

    // A multi-turn spawn test must pick each parent reply from the request, not from
    // its position. The child result can reach the parent before or after the
    // post-spawn model call, and the session accepts both orders. If the result
    // arrives first, it folds into the post-spawn call and that call returns the
    // marker. A positional reply queue then shifts by one, and the next turn gets a
    // stale text reply in place of its spawn_agent call.
    private string PrepareParentSpawnReplies(IReadOnlyList<(string Prompt, FunctionCallContent Spawn)> spawns)
    {
        var marker = PrepareParentResumeMarker();
        var resumeSelector = _clientProvider.Main.ResponseContentsSelector!;
        _clientProvider.Main.ResponseContentsSelector = request =>
        {
            var issuedCallIds = request
                .SelectMany(static message => message.Contents.OfType<FunctionCallContent>())
                .Select(static call => call.CallId)
                .ToHashSet(StringComparer.Ordinal);
            var userTexts = request
                .Where(static message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
                .Select(static message => message.Text)
                .ToArray();
            foreach (var (prompt, spawn) in spawns)
            {
                if (!issuedCallIds.Contains(spawn.CallId)
                    && userTexts.Any(text => text.Contains(prompt, StringComparison.Ordinal)))
                    return [spawn];
            }

            return resumeSelector(request) ?? [new TextContent("Child run accepted.")];
        };
        return marker;
    }

    private static int CountDeliveredChildRuns(IReadOnlyList<ChatMessage> request)
    {
        var acceptedRunIds = FindAcceptedRunIds(request);
        var deliveredRunIds = FindDeliveredRunIds(request);
        Assert.All(deliveredRunIds, runId => Assert.Contains(runId, acceptedRunIds));
        return deliveredRunIds.Count;
    }

    // A child result reaches the parent as a notice: a user message that names the run and its result file.
    private static IEnumerable<string> NoticeTexts(IEnumerable<ChatMessage> request)
        => request
            .Where(static message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                && message.Text.StartsWith("Child run ended.", StringComparison.Ordinal))
            .Select(static message => message.Text);

    private static string NoticeRunId(string notice)
        => System.Text.RegularExpressions.Regex.Match(notice, @"\brun=(\S+)").Groups[1].Value;

    private static string ReadNoticeFile(string notice)
    {
        var path = System.Text.RegularExpressions.Regex.Match(notice, @"result_file=(\S+)").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(path), "The notice names no result file.");
        return File.ReadAllText(path);
    }

    private static HashSet<string> FindAcceptedRunIds(IEnumerable<ChatMessage> request)
    {
        var contents = request.SelectMany(static message => message.Contents).ToArray();
        var resultsByCallId = contents
            .OfType<FunctionResultContent>()
            .ToLookup(static result => result.CallId, StringComparer.Ordinal);
        var toolAcceptedRunIds = contents
            .OfType<FunctionCallContent>()
            .Where(static call => call.Name == "spawn_agent")
            .SelectMany(call => resultsByCallId[call.CallId]
                .Select(static result => ParseAcceptedRunId(result.Result?.ToString())))
            .Where(static runId => !string.IsNullOrWhiteSpace(runId))
            .Select(static runId => runId!)
            .ToHashSet(StringComparer.Ordinal);

        var routedAcceptedRunIds = request
            .Where(static message => message.Role == Microsoft.Extensions.AI.ChatRole.Assistant)
            .SelectMany(static message => message.Contents.OfType<Microsoft.Extensions.AI.TextContent>())
            .Select(static text => ParseAcceptedRunId(text.Text))
            .Where(static runId => !string.IsNullOrWhiteSpace(runId))
            .Select(static runId => runId!);

        toolAcceptedRunIds.UnionWith(routedAcceptedRunIds);
        return toolAcceptedRunIds;
    }

    // Review Should-fix 3: a run gets exactly one notice. The set below would hide a second notice for one run, so
    // the notices are counted before the set removes repeats.
    private static HashSet<string> FindDeliveredRunIds(IEnumerable<ChatMessage> request)
    {
        var noticeRunIds = NoticeTexts(request).Select(NoticeRunId).ToArray();
        var deliveredRunIds = noticeRunIds.ToHashSet(StringComparer.Ordinal);
        Assert.True(noticeRunIds.Length == deliveredRunIds.Count,
            $"A run has more than one notice in one model request: {string.Join(", ", noticeRunIds)}.");
        return deliveredRunIds;
    }

    private static string? ParseAcceptedRunId(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return null;

        try
        {
            using var json = JsonDocument.Parse(result);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("state", out var state) ||
                state.ValueKind != JsonValueKind.String || state.GetString() != "accepted" ||
                !root.TryGetProperty("run_id", out var runId) ||
                runId.ValueKind != JsonValueKind.String)
                return null;

            var value = runId.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static string GetChildRunResult(IReadOnlyList<ChatMessage> messages, string runId)
        => ReadNoticeFile(Assert.Single(NoticeTexts(messages), text => NoticeRunId(text) == runId));

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
