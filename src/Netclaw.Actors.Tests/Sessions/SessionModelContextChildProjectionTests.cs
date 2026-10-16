// -----------------------------------------------------------------------
// <copyright file="SessionModelContextChildProjectionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using ProtocolChatRole = Netclaw.Actors.Protocol.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class SessionModelContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_projection_preserves_canonical_history_and_context_until_the_durable_decision(bool failInspection)
    {
        // Arrange
        var owner = CreateOwner(new StaticSystemPromptProvider("stable prompt"),
            snapshots: failInspection ? new FailingSnapshotProvider() : new ImmediateSnapshotProvider());
        owner.SetOverlay("retained context note");
        var runId = new SubAgentRunId("projection-run");
        var callId = new ToolCallId("child-result-projection-run");
        var delta = new WorkingContextDelta { ConfirmedChangedFiles = ["/repo/child.cs"] };
        var call = new SerializableChatMessage
        {
            Role = ProtocolChatRole.Assistant,
            ToolCalls = [new SerializableToolCall
            {
                CallId = callId, Name = new ToolName("child_run_result"), ArgumentsJson = "{}"
            }]
        };
        var result = new SerializableChatMessage
        {
            Role = ProtocolChatRole.Tool, Name = "child_run_result", ToolCallId = callId,
            Content = "Independent child result."
        };
        var terminal = new ChildRunTerminal(new SubAgentProtocol.SubAgentResult
        {
            AgentName = new AgentName("worker"), RunId = runId,
            Completion = new ChildRunCompletion.Completed(delta), Output = result.Content
        }, false, null);
        var run = new BackgroundChildRun
        {
            RunId = runId, ScopeId = new SubAgentScopeId("projection-scope"),
            StartKey = new ChildRunStartKey.Tool(SessionId, "original-turn", new ToolCallId("spawn-call")),
            AgentName = new AgentName("worker"), SourceOperation = "spawn_agent", ArgumentsDigest = "digest",
            OriginalContext = new TurnContextRecord { SessionId = SessionId, TurnId = "original-turn" },
            InitialWorkingSnapshot = null, AcceptedAtMs = 1,
            Lifecycle = new ChildRunLifecycle.Prepared(new ChildPreparedResult(terminal, call, result, delta))
        };
        var state = SessionState.Empty.AddUserMessage("Start the child.") with
        {
            ChildRuns = SessionState.Empty.ChildRuns.Add(runId, run),
            WorkingContext = WorkingContext.Empty.WithProjectDirectory("/repo")
        };
        var continuation = new ChildContinuation(runId, callId);
        ContextCall StartChild(long generation, SessionState current) => owner.StartCall(
            generation, current, continuation, admittedTurn: null, Source(TrustAudience.Personal),
            toolTrust: null, recallQuery: null, skillBody: null,
            restartNotice: null, CancellationToken.None);

        // Act
        var inspection = Assert.IsType<ContextCall.Inspect>(StartChild(1, state));
        var reply = await inspection.Work;
        var first = Assert.IsType<PreparedModelRequest>(reply switch
        {
            WorkingContextSnapshotReady ready => owner.CompleteInspection(
                ready, state, continuation, TrustAudience.Personal, currentToolTrust: null),
            WorkingContextSnapshotFailed failed => owner.CompleteInspection(
                failed, state, continuation, TrustAudience.Personal, currentToolTrust: null),
            _ => throw new InvalidOperationException("Unexpected context reply.")
        });
        var cached = Assert.IsType<ContextCall.Ready>(StartChild(2, first.State)).Request;
        var committed = cached.State.Apply(new TurnRecorded
        {
            SessionId = SessionId, UserMessage = state.History[0],
            AssistantReply = new SerializableChatMessage { Role = ProtocolChatRole.Assistant, Content = "Child received." },
            ChildContinuation = continuation, RecordedAtMs = 2
        });
        var afterDecision = Assert.IsType<ContextCall.Ready>(StartChild(3, committed)).Request;

        // Assert
        foreach (var request in new[] { first, cached, afterDecision })
        {
            Assert.Single(request.Messages.SelectMany(message => message.Contents.OfType<FunctionCallContent>()),
                item => item.CallId == callId.Value);
            Assert.Equal(result.Content, Assert.Single(
                request.Messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
                item => item.CallId == callId.Value).Result);
            Assert.Equal(1, NudgeCount(request.State));
            Assert.Contains("retained context note", string.Join("\n", request.Messages.Select(message => message.Text)));
        }
        foreach (var request in new[] { first, cached })
        {
            Assert.DoesNotContain(request.State.History.SelectMany(message => message.ToolCalls), item => item.CallId == callId);
            Assert.DoesNotContain(request.State.History, message => message.ToolCallId == callId);
            Assert.DoesNotContain("/repo/child.cs", request.State.WorkingContext.RecentFiles);
            Assert.IsType<ChildRunLifecycle.Prepared>(request.State.ChildRuns[runId].Lifecycle);
        }
        Assert.Same(first.State, cached.State);
        Assert.Single(committed.History.SelectMany(message => message.ToolCalls), item => item.CallId == callId);
        Assert.Single(committed.History, message => message.ToolCallId == callId);
        Assert.Contains("/repo/child.cs", committed.WorkingContext.RecentFiles);
        Assert.IsType<ChildRunLifecycle.Closed>(committed.ChildRuns[runId].Lifecycle);
    }
}
