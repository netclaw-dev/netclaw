// -----------------------------------------------------------------------
// <copyright file="SessionToolWorkflowTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Authorization.Consent;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using Xunit;
using ChatRole = Netclaw.Actors.Protocol.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class SessionToolWorkflowTests
{
    [Fact]
    public void Restart_parking_requires_durable_authority_for_each_unfinished_call()
    {
        // Arrange
        var workflow = new SessionToolWorkflow();
        var first = CreateRequest("call-first", requestedAtMs: 10);
        var second = CreateRequest("call-second", requestedAtMs: 20);
        workflow.StartDispatch(
        [
            new FunctionCallContent(first.CallId, "shell_execute"),
            new FunctionCallContent(second.CallId, "shell_execute")
        ], cycle: null, executor: null, recordCompletedCycle: false);

        // Act and assert
        Assert.False(workflow.CanParkForRestart());
        workflow.ApplyApprovalRequested(first, durable: true, recovered: false);
        Assert.False(workflow.CanParkForRestart());
        workflow.ApplyApprovalRequested(second, durable: true, recovered: false);
        Assert.True(workflow.CanParkForRestart());

        var legacy = second with { TurnContext = null };
        workflow.ApplyApprovalRequested(legacy, durable: true, recovered: true);
        Assert.False(workflow.CanParkForRestart());

        var incomplete = legacy with { ChannelType = null };
        workflow.ApplyApprovalRequested(incomplete, durable: true, recovered: true);
        Assert.False(workflow.CanParkForRestart());
    }

    [Fact]
    public void Snapshot_remains_unsafe_until_the_approval_batch_clears()
    {
        // Arrange
        var workflow = new SessionToolWorkflow();
        var state = SessionState.Empty;
        var request = CreateRequest("call-snapshot", requestedAtMs: 10);

        // Act and assert
        Assert.True(workflow.AllowsSnapshot(state));
        workflow.ApplyApprovalRequested(request, durable: true, recovered: false);
        Assert.False(workflow.AllowsSnapshot(state));
        var accepted = Assert.IsAssignableFrom<ApprovalResponseResult.DurableAccepted>(
            workflow.PrepareRecoveredResponse(
                CreateResponse(request, "user-1", ApprovalOptionKeys.ApproveOnce), state));
        var resolution = workflow.CreateResolution(accepted, request.SessionId, nowMs: 20);
        workflow.ApplyResolvedResponse(resolution);
        Assert.False(workflow.AllowsSnapshot(state));
        workflow.ClearNormalBatch();
        Assert.True(workflow.AllowsSnapshot(state));
    }

    [Fact]
    public void Abandonment_closes_only_calls_without_a_committed_result()
    {
        // Arrange
        var workflow = new SessionToolWorkflow();
        var sessionId = new SessionId("session-1");
        var assistant = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            [
                new SerializableToolCall { CallId = new ToolCallId("call-A"), Name = new ToolName("file_read") },
                new SerializableToolCall { CallId = new ToolCallId("call-B"), Name = new ToolName("file_read") }
            ]
        };
        var firstResult = new SerializableChatMessage
        {
            Role = ChatRole.Tool,
            ToolCallId = new ToolCallId("call-A"),
            Name = "file_read",
            Content = "first result"
        };
        var state = SessionState.Empty with
        {
            History = SessionState.Empty.History.AddRange([assistant, firstResult])
        };

        // Act
        var abandoned = workflow.CreateAbandonment(state, sessionId, "approval expired", nowMs: 30);
        var updated = workflow.ApplyAbandonment(state, abandoned);

        // Assert
        var synthetic = Assert.Single(abandoned.ToolResults);
        Assert.Equal("call-B", synthetic.ToolCallId?.Value);
        Assert.Equal("approval expired", synthetic.Content);
        Assert.Equal(["call-A", "call-B"], updated.History
            .Where(message => message.Role == ChatRole.Tool)
            .Select(message => message.ToolCallId?.Value));
    }

    [Fact]
    public void Resolve_moves_one_call_from_pending_to_resolved()
    {
        var state = new SessionToolWorkflow();
        var request = CreateRequest("call-1", requestedAtMs: 10);

        var pending = state.ApplyApprovalRequested(request, durable: true, recovered: false);

        Assert.Equal(1, state.PendingDiagnosticCount);
        Assert.Equal(0, state.ResolvedDiagnosticCount);
        Assert.Equal(request, pending.Request);

        var resolvedPending = ResolveRecovered(state, request, ApprovalOptionKeys.ApproveOnce);
        Assert.Same(pending, resolvedPending);
        Assert.Equal(0, state.PendingDiagnosticCount);
        Assert.Equal(1, state.ResolvedDiagnosticCount);
        Assert.True(state.ShouldAbandonResolvedAfterRecovery());
        Assert.IsType<ApprovalResponseResult.Rejected>(
            state.PrepareRecoveredResponse(
                CreateResponse(request, "user-1", ApprovalOptionKeys.Deny), SessionState.Empty));
    }

    [Fact]
    public void Concurrent_requests_wait_until_the_last_call_resolves()
    {
        // Arrange
        var state = new SessionToolWorkflow();
        var first = CreateRequest("call-1", requestedAtMs: 10);
        var second = CreateRequest("call-2", requestedAtMs: 20);
        var history = HistoryState(first, second);

        state.ApplyApprovalRequested(first, durable: true, recovered: true);
        state.ApplyApprovalRequested(second, durable: true, recovered: true);

        // Act and assert
        ResolveRecovered(state, first, ApprovalOptionKeys.ApproveOnce);
        Assert.IsType<RecoveredContinuation.WaitForSiblings>(
            state.PlanRecoveredContinuation(history, first.CallId, first.SessionId, nowMs: 30));

        ResolveRecovered(state, second, ApprovalOptionKeys.Deny);
        var dispatch = Assert.IsType<RecoveredContinuation.Dispatch>(
            state.PlanRecoveredContinuation(history, second.CallId, second.SessionId, nowMs: 40));
        Assert.Equal([first.CallId, second.CallId], dispatch.Calls.Select(call => call.CallId));
        var firstCommand = Assert.IsType<JsonElement>(dispatch.Calls[0].Arguments?["command"]);
        var secondCommand = Assert.IsType<JsonElement>(dispatch.Calls[1].Arguments?["command"]);
        Assert.Equal(JsonValueKind.String, firstCommand.ValueKind);
        Assert.Equal(JsonValueKind.String, secondCommand.ValueKind);
        Assert.Equal(first.CallId, firstCommand.GetString());
        Assert.Equal(second.CallId, secondCommand.GetString());
    }

    [Fact]
    public void A_repeated_request_replaces_the_resolved_call_state()
    {
        var state = new SessionToolWorkflow();
        var request = CreateRequest("call-1", requestedAtMs: 10);

        state.ApplyApprovalRequested(request, durable: true, recovered: false);
        ResolveRecovered(state, request, ApprovalOptionKeys.Deny);

        var replacement = request with { RequestedAtMs = 20 };
        state.ApplyApprovalRequested(replacement, durable: true, recovered: false);

        Assert.Equal(1, state.PendingDiagnosticCount);
        Assert.Equal(0, state.ResolvedDiagnosticCount);
        var accepted = Assert.IsAssignableFrom<ApprovalResponseResult.Accepted>(
            state.PrepareRecoveredResponse(
                CreateResponse(replacement, "user-1", ApprovalOptionKeys.ApproveOnce), SessionState.Empty));
        Assert.Equal(20, accepted.Pending.Request.RequestedAtMs);
    }

    [Fact]
    public void An_incomplete_legacy_request_has_no_turn_authority()
    {
        var state = new SessionToolWorkflow();
        var request = new ToolApprovalRequested
        {
            SessionId = new SessionId("session-1"),
            CallId = "legacy-call",
            ToolName = "shell_execute",
            RequesterPrincipal = PrincipalClassification.TrustedInternal
        };

        var pending = state.ApplyApprovalRequested(request, durable: true, recovered: true);

        Assert.Equal(1, state.PendingDiagnosticCount);
        Assert.Null(pending.TurnContext);
        Assert.Equal("legacy approval event is missing channel type", pending.TurnContextRestoreFailure);
        var rejection = Assert.IsType<ApprovalResponseResult.Rejected>(
            state.PrepareRecoveredResponse(
                CreateResponse(request, "user-1", ApprovalOptionKeys.ApproveOnce), SessionState.Empty));
        Assert.Equal(ApprovalNackReasons.PromptExpired, rejection.NackReason);
    }

    [Fact]
    public void A_legacy_prompt_cannot_authorize_a_new_repository_scope()
    {
        Assert.False(ConsentAnswerCodec.IsOffered(
            [], ApprovalOptionKeys.ApproveRepository, "/work/main/.git"));
        Assert.True(ConsentAnswerCodec.IsOffered(
            [], ApprovalOptionKeys.ApproveOnce, repositoryCommonDirectory: null));
        Assert.False(ConsentAnswerCodec.IsOffered(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            ApprovalOptionKeys.ApproveRepository,
            "/work/main/.git"));
        Assert.False(ConsentAnswerCodec.IsOffered(
            [ApprovalOptionKeys.ApproveRepository],
            ApprovalOptionKeys.ApproveRepository,
            repositoryCommonDirectory: null));
        Assert.True(ConsentAnswerCodec.IsOffered(
            [ApprovalOptionKeys.ApproveRepository],
            ApprovalOptionKeys.ApproveRepository,
            "/work/main/.git"));
    }

    [Theory]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentSessionV1, GrantScopeKind.Session)]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentAlwaysV1, GrantScopeKind.Folder)]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentRepositoryV1, GrantScopeKind.Repository)]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentEverywhereV1, GrantScopeKind.Everywhere)]
    public void New_runtime_maps_assignment_option_keys(
        string optionKey,
        GrantScopeKind expected)
        => Assert.Equal(new ConsentAnswer.Grant(expected), ConsentAnswerCodec.FromOptionKey(optionKey));

    // The text answer guard (child-approval-events-and-parent-pause, design D7, as the PR 2 review changed it) applies
    // only when a child prompt is among the prompts that the sender can answer. With parent prompts only, the session
    // keeps its rule from before PR 2: a text answer resolves the latest prompt by its durable request time. The
    // channels resolve the earliest prompt. The coordinator keeps that difference out of the scope of PR 2. The
    // insertion order here is the reverse of the request order, so the spec also proves that the durable request time
    // decides.
    // Planted bug: the guard refuses every set of two or more prompts (PR 2 as reviewed), or the session resolves the
    // earliest prompt or the last prompt that it got.
    [Fact]
    public void Text_answer_with_two_parent_prompts_resolves_the_latest()
    {
        // Arrange
        var state = new SessionToolWorkflow();
        state.ApplyApprovalRequested(CreateRequest("newer", requestedAtMs: 20), durable: true, recovered: true);
        state.ApplyApprovalRequested(CreateRequest("older", requestedAtMs: 10), durable: true, recovered: true);

        // Act
        var found = state.TryResolveTextResponse(TextAnswer("A"), SessionState.Empty, out var response, out var nackReason);

        // Assert
        Assert.True(found);
        Assert.Null(nackReason);
        Assert.Equal("newer", response?.CallId.Value);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, response?.SelectedKey.Value);
    }

    // The guard stays strict for a child prompt. A child prompt does not name the child, so model text can describe one
    // command while a text answer approves another. With a parent prompt and a child prompt, the text answers neither
    // and the session tells the sender to use the buttons. The request order does not matter.
    // Planted bug: the guard checks only parent prompts, or a mixed set resolves the earliest prompt.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Text_answer_with_a_parent_prompt_and_a_child_prompt_resolves_neither(bool childIsEarlier)
    {
        // Arrange
        var state = new SessionToolWorkflow();
        state.ApplyApprovalRequested(
            CreateRequest("parent-call", requestedAtMs: childIsEarlier ? 20 : 10), durable: true, recovered: true);
        state.ApplyApprovalRequested(
            CreateChildRequest("r-guard", requestedAtMs: childIsEarlier ? 10 : 20), durable: true, recovered: true);

        // Act
        var found = state.TryResolveTextResponse(TextAnswer("A"), SessionState.Empty, out var response, out var nackReason);

        // Assert
        Assert.False(found);
        Assert.Null(response);
        Assert.Equal(ApprovalNackReasons.UseButtons, nackReason);
    }

    // A child prompt has a SourceChildRunId and the call ID that the bridge makes from the run ID.
    private static ToolApprovalRequested CreateChildRequest(string runId, long requestedAtMs)
        => CreateRequest(ChildApprovalPrompt.CreateCallId(runId, 1).Value, requestedAtMs) with
        {
            SourceChildRunId = new SubAgentRunId(runId)
        };

    private static ToolInteractionTextResponse TextAnswer(string text) => new()
    {
        SessionId = new SessionId("session-1"),
        Text = text,
        SenderId = new SenderId("user-1")
    };

    [Fact]
    public void Recovered_dispatch_waits_for_siblings_and_retains_each_resolution()
    {
        // Arrange
        var state = new SessionToolWorkflow();
        var approved = CreateRequest("call-approved", requestedAtMs: 10);
        var denied = CreateRequest("call-denied", requestedAtMs: 20) with
        {
            ManagedTemporaryDirectory = "/session/tmp"
        };
        var pending = CreateRequest("call-pending", requestedAtMs: 30);
        var history = HistoryState(approved, denied, pending);
        history = history with
        {
            History = history.History.Add(new SerializableChatMessage
            {
                Role = ChatRole.Tool,
                ToolCallId = new ToolCallId(approved.CallId),
                Name = approved.ToolName,
                Content = "completed result"
            })
        };

        state.ApplyApprovalRequested(approved, durable: true, recovered: true);
        state.ApplyApprovalRequested(denied, durable: true, recovered: true);
        state.ApplyApprovalRequested(pending, durable: true, recovered: true);
        ResolveRecovered(state, approved, ApprovalOptionKeys.ApproveOnce);
        ResolveRecovered(state, denied, ApprovalOptionKeys.Deny);

        // Act and assert
        Assert.IsType<RecoveredContinuation.WaitForSiblings>(
            state.PlanRecoveredContinuation(history, denied.CallId, denied.SessionId, nowMs: 40));
        ResolveRecovered(state, pending, ApprovalOptionKeys.Deny);
        var dispatch = Assert.IsType<RecoveredContinuation.Dispatch>(
            state.PlanRecoveredContinuation(history, pending.CallId, pending.SessionId, nowMs: 50));
        var plan = dispatch.ApprovalPlan;

        Assert.Equal([denied.CallId, pending.CallId], dispatch.Calls.Select(call => call.CallId));
        Assert.NotNull(plan.OneTimeApprovalPreSeed);
        Assert.NotNull(plan.DecisionOverride);
        Assert.NotNull(plan.ManagedTemporaryDenialDirectories);
        Assert.NotNull(plan.AuthorizationAttemptIds);
        Assert.Equal(approved.Patterns, plan.OneTimeApprovalPreSeed[approved.CallId]);
        Assert.Equal(RefusalKind.Denied, plan.DecisionOverride[denied.CallId]);
        Assert.Equal("/session/tmp", plan.ManagedTemporaryDenialDirectories[denied.CallId]);
        var attempts = plan.AuthorizationAttemptIds;
        Assert.Equal(approved.AuthorizationAttemptId, attempts[approved.CallId].Value);
        Assert.Equal(denied.AuthorizationAttemptId, attempts[denied.CallId].Value);
        Assert.Equal(pending.AuthorizationAttemptId, attempts[pending.CallId].Value);
    }

    [Fact]
    public void Resolved_assignment_grant_redrives_only_the_exact_call_once()
    {
        var digest = new ApprovalAssignmentDigest($"sha256:{new string('a', 64)}");
        var candidate = new ApprovalCandidate(
            "inspect",
            "/work/repository")
        {
            AssignmentDigest = digest,
            Shell = ApprovalShell.Bash,
            VerbTokens = ["inspect"],
        };
        var request = CreateRequest("call-assignment", requestedAtMs: 10) with
        {
            Patterns = ["inspect item"],
            CandidateVerbs = ["inspect"],
            Candidates = [candidate],
            Cwd = "/work/repository",
            OptionKeys =
            [
                ApprovalOptionKeys.ApproveOnce,
                ApprovalOptionKeys.ApproveAssignmentAlwaysV1,
                ApprovalOptionKeys.Deny,
            ],
        };
        var state = new SessionToolWorkflow();
        state.ApplyApprovalRequested(request, durable: true, recovered: true);
        ResolveRecovered(state, request, ApprovalOptionKeys.ApproveAssignmentAlwaysV1);

        var dispatch = Assert.IsType<RecoveredContinuation.Dispatch>(
            state.PlanRecoveredContinuation(HistoryState(request), request.CallId, request.SessionId, nowMs: 30));
        var plan = dispatch.ApprovalPlan;

        Assert.Equal(
            OneTimeApprovalKeys.Create(request.Patterns, request.Candidates, request.Cwd),
            plan.OneTimeApprovalPreSeed![request.CallId]);
        Assert.Null(plan.DecisionOverride);
    }

    private static SessionState HistoryState(params ToolApprovalRequested[] requests)
    {
        var assistant = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls = requests.Select(request => new SerializableToolCall
            {
                CallId = new ToolCallId(request.CallId),
                Name = new ToolName(request.ToolName),
                ArgumentsJson = $"{{\"command\":\"{request.CallId}\"}}"
            }).ToArray()
        };
        return SessionState.Empty with { History = SessionState.Empty.History.Add(assistant) };
    }

    private static PendingToolInteraction ResolveRecovered(
        SessionToolWorkflow workflow,
        ToolApprovalRequested request,
        string optionKey)
    {
        var accepted = Assert.IsAssignableFrom<ApprovalResponseResult.DurableAccepted>(
            workflow.PrepareRecoveredResponse(
                CreateResponse(request, "user-1", optionKey), SessionState.Empty));
        workflow.ApplyResolvedResponse(workflow.CreateResolution(accepted, request.SessionId, nowMs: 30));
        return accepted.Pending;
    }
}
