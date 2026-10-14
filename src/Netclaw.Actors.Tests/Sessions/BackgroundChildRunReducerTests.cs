// -----------------------------------------------------------------------
// <copyright file="BackgroundChildRunReducerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class BackgroundChildRunReducerTests
{
    private static readonly SessionId Owner = new("test/child-run-owner");

    [Fact]
    public void Accepted_tool_call_keeps_one_original_result_and_rejects_duplicate_start()
    {
        var run = CreateRun(toolCallId: "spawn-call");
        var originalCall = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            [
                new SerializableToolCall
                {
                    CallId = new ToolCallId("spawn-call"),
                    Name = new ToolName("spawn_agent"),
                    ArgumentsJson = "{}"
                }
            ]
        };
        var state = SessionState.Empty with { History = [originalCall] };
        var accepted = new ChildRunAccepted
        {
            SessionId = Owner,
            Run = run,
            AcceptanceResult = new SerializableChatMessage
            {
                Role = ChatRole.Tool,
                Name = "spawn_agent",
                ToolCallId = new ToolCallId("spawn-call"),
                Content = "{\"state\":\"accepted\",\"run_id\":\"run-1\"}"
            },
            RecordedAtMs = 10
        };

        var recorded = state.Apply(accepted);

        Assert.Single(recorded.ChildRuns);
        Assert.Equal(2, recorded.History.Count);
        Assert.Equal("spawn-call", recorded.History[1].ToolCallId?.Value);
        Assert.Throws<InvalidOperationException>(() => recorded.Apply(accepted with
        {
            Run = run with { RunId = new SubAgentRunId("run-2"), ScopeId = new SubAgentScopeId("scope-2") }
        }));
        Assert.Single(recorded.ChildRuns);
        Assert.Equal(2, recorded.History.Count);
    }

    [Fact]
    public void Child_pair_commits_once_without_adding_an_ordinary_user_message()
    {
        var run = CreateRun(toolCallId: null);
        var accepted = SessionState.Empty.Apply(new ChildRunAccepted
        {
            SessionId = Owner,
            Run = run,
            RecordedAtMs = 10
        });
        var terminal = CreateTerminal(run);
        var terminalState = accepted.Apply(new ChildRunEvent.TerminalRecorded(terminal)
        {
            SessionId = Owner,
            RunId = run.RunId,
            RecordedAtMs = 11
        });
        var callId = new ToolCallId("child-result-call");
        var assistantCall = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            [
                new SerializableToolCall
                {
                    CallId = callId,
                    Name = new ToolName("child_run_result"),
                    ArgumentsJson = "{}"
                }
            ]
        };
        var toolResult = new SerializableChatMessage
        {
            Role = ChatRole.Tool,
            Name = "child_run_result",
            ToolCallId = callId,
            Content = "Child result."
        };
        var prepared = terminalState.Apply(new ChildRunEvent.ResultPrepared(assistantCall, toolResult, terminal.Result.WorkingContext)
        {
            SessionId = Owner,
            RunId = run.RunId,
            RecordedAtMs = 12
        });

        var committed = prepared.Apply(new TurnRecorded
        {
            SessionId = Owner,
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Must not be added." },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Done." },
            ChildContinuation = new ChildContinuation(run.RunId, callId),
            RecordedAtMs = 13
        });

        Assert.Equal([assistantCall, toolResult, new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Done." }], committed.History);
        var closed = Assert.IsType<ChildRunLifecycle.Closed>(committed.ChildRuns[run.RunId].Lifecycle);
        Assert.Equal(callId, closed.DeliveryCallId);
        Assert.Equal(SubAgentRunOutcome.Completed, closed.Outcome);
        Assert.Equal("Durable finding", Assert.Single(terminal.Result.Findings).Title);
        Assert.Equal("/repo/after.cs", terminal.Result.WorkingContext?.ReadFiles.Single());
    }

    private static BackgroundChildRun CreateRun(string? toolCallId)
    {
        var context = new TurnContextRecord
        {
            SessionId = Owner,
            TurnId = "turn-1",
            Boundary = TrustBoundary.Public,
            RequesterPrincipal = PrincipalClassification.UntrustedExternal
        };
        ChildRunStartKey start = toolCallId is null
            ? new ChildRunStartKey.Slash(Owner, "turn-1", new InputId("slash-input"))
            : new ChildRunStartKey.Tool(Owner, "turn-1", new ToolCallId(toolCallId));
        return new BackgroundChildRun
        {
            RunId = new SubAgentRunId("run-1"),
            ScopeId = new SubAgentScopeId("scope-1"),
            StartKey = start,
            AgentName = new AgentName("worker"),
            SourceOperation = toolCallId is null ? "slash" : "spawn_agent",
            ArgumentsDigest = "digest-1",
            OriginalContext = context,
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = new WorkingContext { ProjectDirectory = "/repo/before" },
                Git = new GitWorkingContextInspection.Skipped()
            },
            AcceptedAtMs = 10,
            Lifecycle = new ChildRunLifecycle.Accepted(null)
        };
    }

    private static ChildRunTerminal CreateTerminal(BackgroundChildRun run)
    {
        var delta = new WorkingContextDelta
        {
            ProjectDirectory = "/repo/after",
            ReadFiles = ["/repo/after.cs"],
            ConfirmedChangedFiles = ["/repo/changed.cs"]
        };
        var result = new SubAgentProtocol.SubAgentResult
        {
            AgentName = run.AgentName,
            RunId = run.RunId,
            ScopeId = run.ScopeId,
            Output = "Completed work.",
            Completion = new ChildRunCompletion.Completed(delta),
            Findings =
            [
                new SubAgentFinding
                {
                    Title = "Durable finding",
                    Content = "The child found a durable fact.",
                    Evidence = ["/repo/changed.cs"]
                }
            ],
            FindingsCount = 1
        };
        return new ChildRunTerminal(result, Lost: false, EvidenceWarning: null);
    }
}
