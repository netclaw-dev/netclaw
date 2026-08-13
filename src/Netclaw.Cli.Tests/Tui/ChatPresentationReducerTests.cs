// -----------------------------------------------------------------------
// <copyright file="ChatPresentationReducerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Cli.Tui;
using Netclaw.Media;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ChatPresentationReducerTests
{
    private static readonly SessionId SessionId = new("test/chat");

    [Fact]
    public void Parallel_tool_results_keep_stable_rows_until_the_turn_settles()
    {
        var state = ChatPresentationState.Empty;
        state = Apply(state, ToolCall("call-a", "search", 1));
        state = Apply(state, ToolCall("call-b", "search", 2));

        var second = ChatPresentationReducer.Reduce(state, ToolResult("call-b", "result-b", 3));

        Assert.True(second.State.Tools.ContainsKey("call-a"));
        Assert.True(second.State.Tools.ContainsKey("call-b"));
        Assert.Equal("completed", second.State.Tools["call-b"].Phase);
        Assert.Equal("result-b", second.State.Tools["call-b"].Result);
        Assert.Empty(second.Effects.OfType<ChatPresentationEffect.Commit>());

        var first = ChatPresentationReducer.Reduce(second.State, ToolResult("call-a", "result-a", 4));

        Assert.Equal(2, first.State.Tools.Count);
        Assert.All(first.State.Tools.Values, tool => Assert.NotNull(tool.CompletedAtMs));

        var settled = ChatPresentationReducer.Reduce(first.State, CompletedTurn(5));

        Assert.Empty(settled.State.Tools);
        var reply = Assert.Single(settled.State.Transcript);
        Assert.Equal(ChatBlockKind.Assistant, reply.Kind);
        Assert.Contains("result-b", reply.SemanticText, StringComparison.Ordinal);
        Assert.Contains("result-a", reply.SemanticText, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_rationale_is_the_work_title_and_arguments_do_not_supply_a_fallback()
    {
        var withRationale = ToolCall("call-a", "search", 1) with
        {
            Rationale = "Find the relevant source"
        };
        var state = Apply(ChatPresentationState.Empty, withRationale);

        Assert.Equal("Find the relevant source", state.Tools["call-a"].Rationale);

        state = Apply(state, ToolResult("call-a", "result-a", 2));
        Assert.Equal("Find the relevant source", state.Tools["call-a"].Rationale);

        var missingState = Apply(ChatPresentationState.Empty, ToolCall("call-b", "search", 3));
        missingState = Apply(missingState, ToolResult("call-b", "result-b", 4));
        missingState = Apply(missingState, CompletedTurn(5));
        var missing = Assert.Single(missingState.Transcript);
        Assert.Contains("No rationale supplied", missing.SemanticText, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_rationale_result_marks_the_tool_request_as_rejected()
    {
        var state = Apply(ChatPresentationState.Empty, ToolCall("call-a", "search", 1) with
        {
            FailureCode = "invalid_rationale"
        });
        Assert.Equal("rejected", state.Tools["call-a"].Phase);

        state = Apply(state, ToolResult("call-a", "The tool was not executed.", 2) with
        {
            FailureCode = "invalid_rationale"
        });

        var tool = state.Tools["call-a"];
        Assert.Equal("rejected", tool.Phase);
        Assert.Equal("Rejected tool request · rationale missing",
            ChatPresentationReducer.ToolWorkTitle(tool));

        state = Apply(state, CompletedTurn(3));
        Assert.Contains("1 rejected request", Assert.Single(state.Transcript).Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parallel_tool_batch_stays_in_one_live_passage()
    {
        var first = ToolCall("call-a", "search", 1) with { BatchId = "batch-1", BatchSize = 2 };
        var second = ToolCall("call-b", "fetch", 2) with { BatchId = "batch-1", BatchSize = 2 };

        var state = Apply(ChatPresentationState.Empty, first);
        state = Apply(state, second);

        var passage = Assert.Single(state.ReplyPassages);
        Assert.Equal(["call-a", "call-b"], passage.ToolCallIds);
        Assert.Empty(state.Transcript);
    }

    [Fact]
    public void Parallel_same_name_subagents_keep_distinct_run_rows()
    {
        var state = ChatPresentationState.Empty;
        state = Apply(state, SubAgent("run-a", SubAgentPhase.Started, 1));
        state = Apply(state, SubAgent("run-b", SubAgentPhase.Started, 2));
        state = Apply(state, SubAgent("run-b", SubAgentPhase.Activity, 3, "reading"));

        Assert.Equal(2, state.SubAgents.Count);
        Assert.Equal("reading", state.SubAgents["run-b"].Phase);
        Assert.Equal("started", state.SubAgents["run-a"].Phase);

        state = Apply(state, SubAgent("run-a", SubAgentPhase.Completed, 4));

        Assert.True(state.SubAgents.ContainsKey("run-a"));
        Assert.Equal("completed", state.SubAgents["run-a"].Phase);
        Assert.True(state.SubAgents.ContainsKey("run-b"));
        Assert.Empty(state.Transcript);
    }

    [Fact]
    public void Subagent_tool_identity_survives_the_approval_phase()
    {
        var state = Apply(ChatPresentationState.Empty, SubAgent("run-a", SubAgentPhase.Started, 1));
        state = Apply(state, SubAgent("run-a", SubAgentPhase.Activity, 2, "running tools: shell_execute"));
        state = Apply(state, SubAgent("run-a", SubAgentPhase.Activity, 3, "awaiting human approval"));

        Assert.Equal("shell_execute", state.SubAgents["run-a"].ActiveToolName);
        Assert.Equal("awaiting human approval", state.SubAgents["run-a"].Phase);
    }

    [Fact]
    public void Approval_outcome_removes_only_its_request_and_commits_the_decision()
    {
        const string firstCallId = "parent-a/subagent-approval/approval-a";
        const string secondCallId = "parent-b/subagent-approval/approval-b";
        var state = Apply(ChatPresentationState.Empty, Approval(firstCallId, 1));
        state = Apply(state, Approval(secondCallId, 2));
        state = Apply(state, Approval(firstCallId, 3));

        Assert.Equal(2, state.PendingApprovalCount);
        Assert.Equal(1, state.ApprovalQueuePosition(firstCallId));
        Assert.Equal(2, state.ApprovalQueuePosition(secondCallId));

        state = Apply(state, new ApprovalOutcomeOutput
        {
            SessionId = SessionId,
            TimestampMs = 4,
            CallId = new ToolCallId(firstCallId),
            ToolName = new ToolName("shell_execute"),
            ParentCallId = "parent-a",
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.Deny)
        });

        Assert.Equal(secondCallId, state.PendingApproval?.CallId.Value);
        Assert.Equal(1, state.ApprovalQueuePosition(secondCallId));
        Assert.Empty(state.Transcript);
        var decision = Assert.Single(state.CompletedApprovals);
        Assert.True(decision.IsFailure);
        Assert.Contains("denied", decision.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Transient_thought_and_tool_activity_stay_out_of_settled_transcript()
    {
        var state = Apply(ChatPresentationState.Empty, new ThinkingDeltaOutput("private step")
        {
            SessionId = SessionId,
            TimestampMs = 1
        });
        state = Apply(state, ToolCall("call-a", "search", 2));
        state = Apply(state, new ToolActivityOutput
        {
            SessionId = SessionId,
            TimestampMs = 3,
            CallId = new ToolCallId("call-a"),
            ToolName = new ToolName("search"),
            TurnId = new TurnId("turn-1"),
            Phase = "running",
            Summary = "query 1"
        });

        Assert.Empty(state.Transcript);
        Assert.Equal("private step", state.ThoughtText);
        Assert.Equal("running", state.Tools["call-a"].Phase);

        state = Apply(state, new ThinkingOutput("short reason")
        {
            SessionId = SessionId,
            TimestampMs = 4
        });

        Assert.Empty(state.Transcript);
        Assert.Equal("short reason", state.ThoughtText);
    }

    [Fact]
    public void Usage_block_shows_reasoning_tokens_and_keeps_complete_detail()
    {
        var state = Apply(ChatPresentationState.Empty, new UsageOutput
        {
            SessionId = SessionId,
            TimestampMs = 10,
            InputTokens = 100,
            OutputTokens = 20,
            CachedInputTokens = 40,
            ReasoningTokens = 12,
            ContextWindowTokens = 1000,
            UsagePercent = 0.1,
            PromptMs = 18,
            PredictedPerSecond = 55
        });

        var usage = Assert.Single(state.Transcript);
        Assert.Contains("12 thought", usage.Summary, StringComparison.Ordinal);
        Assert.Contains("Cached input tokens: 40", usage.Detail, StringComparison.Ordinal);
        Assert.Contains("Speed: 55.0 tokens/s", usage.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_resume_prefers_structured_transcript_over_legacy_messages()
    {
        var state = Apply(ChatPresentationState.Empty, new SessionJoined
        {
            SessionId = SessionId,
            TimestampMs = 10,
            TurnCount = 1,
            RecentMessages = [new ChatMessageDto("assistant", "legacy text")],
            RecentTranscript =
            [
                new SessionTranscriptEntry
                {
                    Type = SessionTranscriptEntryTypes.Tool,
                    CallId = "call-1",
                    ToolName = "status",
                    Rationale = "Check service health",
                    Result = "healthy"
                }
            ]
        });

        Assert.DoesNotContain(state.Transcript, block => block.Summary == "legacy text");
        Assert.Contains(state.Transcript, block =>
            block.Kind == ChatBlockKind.Tool && block.SemanticText.Contains("healthy", StringComparison.Ordinal));
        Assert.Contains(state.Transcript, block =>
            block.Kind == ChatBlockKind.Tool && block.Summary.Contains("Check service health", StringComparison.Ordinal));
    }

    [Fact]
    public void New_session_join_does_not_add_a_redundant_transcript_block()
    {
        var reduction = ChatPresentationReducer.Reduce(ChatPresentationState.Empty, new SessionJoined
        {
            SessionId = SessionId,
            TimestampMs = 10,
            TurnCount = 0
        });

        Assert.True(reduction.State.HasJoined);
        Assert.Empty(reduction.State.Transcript);
        Assert.Empty(reduction.Effects.OfType<ChatPresentationEffect.Commit>());
    }

    [Fact]
    public void Session_title_updates_the_header_and_uses_a_title_block()
    {
        var reduction = ChatPresentationReducer.Reduce(ChatPresentationState.Empty, new SessionTitleOutput("Review the release")
        {
            SessionId = SessionId,
            TimestampMs = 10
        });

        Assert.Equal("Review the release", reduction.State.SessionTitle);
        var block = Assert.Single(reduction.Effects.OfType<ChatPresentationEffect.Commit>()).Block;
        Assert.Equal("TITLE", block.Label);
    }

    [Fact]
    public void Unsupported_output_commits_a_visible_diagnostic()
    {
        var state = Apply(ChatPresentationState.Empty, new UnknownOutput
        {
            SessionId = SessionId,
            TimestampMs = 9
        });

        var diagnostic = Assert.Single(state.Transcript);
        Assert.Equal(ChatBlockKind.Diagnostic, diagnostic.Kind);
        Assert.Contains(nameof(UnknownOutput), diagnostic.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_turn_settles_incomplete_activity_as_diagnostics()
    {
        var state = Apply(ChatPresentationState.Empty, ToolCall("call-a", "search", 1));
        state = Apply(state, SubAgent("run-a", SubAgentPhase.Started, 2));

        state = Apply(state, new TurnCompleted
        {
            SessionId = SessionId,
            TimestampMs = 3,
            TurnNumber = new TurnNumber(1),
            Outcome = TurnOutcome.Failed
        });

        Assert.Empty(state.Tools);
        Assert.Empty(state.SubAgents);
        Assert.Single(state.Transcript, block => block.Kind == ChatBlockKind.Assistant);
        Assert.Single(state.Transcript, block => block.Kind == ChatBlockKind.Diagnostic);
    }

    [Fact]
    public void Sequential_model_steps_settle_as_one_ordered_reply()
    {
        var state = Apply(ChatPresentationState.Empty, new TextDeltaOutput("I will inspect the source.")
        {
            SessionId = SessionId,
            TimestampMs = 1
        });
        state = Apply(state, new TextOutput("I will inspect the source.")
        {
            SessionId = SessionId,
            TimestampMs = 2
        });
        state = Apply(state, ToolCall("call-a", "search", 3) with
        {
            Rationale = "Find the source"
        });
        state = Apply(state, ToolResult("call-a", "source found", 4));
        state = Apply(state, new TextDeltaOutput("The source confirms the behavior.")
        {
            SessionId = SessionId,
            TimestampMs = 5
        });
        state = Apply(state, new TextOutput("The source confirms the behavior.")
        {
            SessionId = SessionId,
            TimestampMs = 6
        });

        Assert.Equal(2, state.ReplyPassages.Count);
        Assert.Empty(state.Transcript);
        Assert.Equal("I will inspect the source.", state.ReplyPassages[0].Text);
        Assert.Equal("The source confirms the behavior.", state.ReplyPassages[1].Text);
        Assert.True(state.Tools.ContainsKey("call-a"));

        state = Apply(state, CompletedTurn(7));

        var reply = Assert.Single(state.Transcript);
        Assert.True(reply.Summary.IndexOf("I will inspect", StringComparison.Ordinal)
                    < reply.Summary.IndexOf("The source confirms", StringComparison.Ordinal));
        Assert.Contains("Completed work  · 1 tool", reply.Summary, StringComparison.Ordinal);
        Assert.Contains("Find the source", reply.SemanticText, StringComparison.Ordinal);
        Assert.Empty(state.ReplyPassages);
    }

    [Fact]
    public void Every_session_output_subtype_has_a_defined_reduction()
    {
        SessionOutput[] outputs =
        [
            new SessionJoined { SessionId = SessionId },
            new TextOutput("answer") { SessionId = SessionId },
            new TextDeltaOutput("part") { SessionId = SessionId },
            new ThinkingOutput("reason") { SessionId = SessionId },
            new ThinkingDeltaOutput("step") { SessionId = SessionId },
            ToolCall("call-a", "search", 1),
            ToolResult("call-a", "result", 2),
            new ToolActivityOutput
            {
                SessionId = SessionId,
                CallId = new ToolCallId("call-a"),
                ToolName = new ToolName("search"),
                TurnId = new TurnId("turn-a"),
                Phase = "running"
            },
            new UsageOutput { SessionId = SessionId },
            new TurnCompleted
            {
                SessionId = SessionId,
                TurnNumber = new TurnNumber(1)
            },
            new SessionTitleOutput("title") { SessionId = SessionId },
            new ErrorOutput { SessionId = SessionId, Message = "error" },
            new FileOutput
            {
                SessionId = SessionId,
                FilePath = "/tmp/report.txt",
                FileName = "report.txt",
                MimeType = new MimeType("text/plain")
            },
            SubAgent("run-a", SubAgentPhase.Started, 3),
            new BufferFlush { SessionId = SessionId },
            new ProcessingStateOutput(true) { SessionId = SessionId },
            new UserMessageQueuedOutput
            {
                SessionId = SessionId,
                MessageId = "tui:message-1",
                TurnId = new TurnId("turn-a"),
                QueueDepth = 1
            },
            new UserMessagesPulledOutput
            {
                SessionId = SessionId,
                BatchId = "batch-a",
                TurnId = new TurnId("turn-a"),
                Messages = [new PulledUserMessage("tui:message-1", "Use the dev branch")]
            },
            new CompactionOutput
            {
                SessionId = SessionId,
                MessagesBefore = 10,
                MessagesAfter = 4
            },
            Approval("approval-a", 4),
            new ApprovalOutcomeOutput
            {
                SessionId = SessionId,
                CallId = new ToolCallId("approval-a"),
                ToolName = new ToolName("shell_execute"),
                SelectedKey = ApprovalOptionKeys.ApproveOnceKey
            }
        ];

        var discoveredTypes = typeof(SessionOutput).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(SessionOutput).IsAssignableFrom(type))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var coveredTypes = outputs.Select(output => output.GetType().Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(discoveredTypes, coveredTypes);
        foreach (var output in outputs)
        {
            var reduction = ChatPresentationReducer.Reduce(ChatPresentationState.Empty, output);
            Assert.DoesNotContain(reduction.State.Transcript, block =>
                block.Kind == ChatBlockKind.Diagnostic
                && block.Summary.StartsWith("Unsupported session output", StringComparison.Ordinal));
        }
    }

    private static ChatPresentationState Apply(ChatPresentationState state, SessionOutput output) =>
        ChatPresentationReducer.Reduce(state, output).State;

    private static ToolCallOutput ToolCall(string callId, string name, long timestamp) => new()
    {
        SessionId = SessionId,
        TimestampMs = timestamp,
        CallId = new ToolCallId(callId),
        ToolName = new ToolName(name),
        ArgumentsJson = $"{{\"call\":\"{callId}\"}}"
    };

    private static ToolResultOutput ToolResult(string callId, string result, long timestamp) => new()
    {
        SessionId = SessionId,
        TimestampMs = timestamp,
        CallId = new ToolCallId(callId),
        ToolName = new ToolName("search"),
        Result = result
    };

    private static TurnCompleted CompletedTurn(long timestamp) => new()
    {
        SessionId = SessionId,
        TimestampMs = timestamp,
        TurnNumber = new TurnNumber(1),
        Outcome = TurnOutcome.Completed
    };

    private static ToolInteractionRequest Approval(string callId, long timestamp) => new()
    {
        SessionId = SessionId,
        TimestampMs = timestamp,
        Kind = "approval",
        CallId = new ToolCallId(callId),
        ToolName = new ToolName("shell_execute"),
        DisplayText = "dotnet test",
        Options = [new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)]
    };

    private static SubAgentOutput SubAgent(
        string runId,
        SubAgentPhase phase,
        long timestamp,
        string? activityPhase = null) => new()
        {
            SessionId = SessionId,
            TimestampMs = timestamp,
            AgentName = new AgentName("reviewer"),
            Phase = phase,
            RunId = new SubAgentRunId(runId),
            ParentCallId = new ToolCallId("parent"),
            ActivityPhase = activityPhase,
            Success = true,
            Outcome = SubAgentRunOutcome.Completed,
            Duration = TimeSpan.FromSeconds(2)
        };

    private sealed record UnknownOutput : SessionOutput;
}
