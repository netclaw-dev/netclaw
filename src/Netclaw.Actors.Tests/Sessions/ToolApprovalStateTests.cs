// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolApprovalStateTests
{
    [Fact]
    public void Resolve_moves_one_call_from_pending_to_resolved()
    {
        var state = new ToolApprovalState();
        var request = CreateRequest("call-1", requestedAtMs: 10);

        var pending = state.Request(request, persistApprovalState: true, recovered: false);

        Assert.Equal(1, state.PendingCount);
        Assert.Equal(0, state.ResolvedCount);
        Assert.Equal(ApprovalTurnPhase.Waiting, state.TurnPhase);
        Assert.Equal(request, pending.Request);

        Assert.True(state.Resolve(request.CallId, ApprovalDecision.ApprovedOnce, out var resolvedPending));
        Assert.Same(pending, resolvedPending);
        Assert.Equal(0, state.PendingCount);
        Assert.Equal(1, state.ResolvedCount);
        Assert.Equal(ApprovalTurnPhase.Running, state.TurnPhase);
        Assert.False(state.Resolve(request.CallId, ApprovalDecision.Denied, out _));
    }

    [Fact]
    public void Concurrent_requests_wait_until_the_last_call_resolves()
    {
        var state = new ToolApprovalState();
        var first = CreateRequest("call-1", requestedAtMs: 10);
        var second = CreateRequest("call-2", requestedAtMs: 20);

        state.Request(first, persistApprovalState: true, recovered: true);
        state.Request(second, persistApprovalState: true, recovered: true);

        Assert.Equal(ApprovalTurnPhase.RecoveredWaiting, state.TurnPhase);
        Assert.True(state.Resolve(first.CallId, ApprovalDecision.ApprovedOnce, out _));
        Assert.Equal(ApprovalTurnPhase.RecoveredWaiting, state.TurnPhase);
        Assert.Equal(1, state.PendingCount);

        Assert.True(state.Resolve(second.CallId, ApprovalDecision.Denied, out _));
        Assert.Equal(ApprovalTurnPhase.Running, state.TurnPhase);
        Assert.Equal(0, state.PendingCount);
        Assert.Equal(2, state.ResolvedCount);
    }

    [Fact]
    public void A_repeated_request_replaces_the_resolved_call_state()
    {
        var state = new ToolApprovalState();
        var request = CreateRequest("call-1", requestedAtMs: 10);

        state.Request(request, persistApprovalState: true, recovered: false);
        Assert.True(state.Resolve(request.CallId, ApprovalDecision.Denied, out _));

        var replacement = request with { RequestedAtMs = 20 };
        state.Request(replacement, persistApprovalState: true, recovered: false);

        Assert.Equal(1, state.PendingCount);
        Assert.Equal(0, state.ResolvedCount);
        Assert.True(state.TryGetPending(request.CallId, out var pending));
        Assert.Equal(20, pending.Request.RequestedAtMs);
    }

    [Fact]
    public void An_incomplete_legacy_request_has_no_turn_authority()
    {
        var state = new ToolApprovalState();
        var request = new ToolApprovalRequested
        {
            SessionId = new SessionId("session-1"),
            CallId = "legacy-call",
            ToolName = "shell_execute",
            RequesterPrincipal = PrincipalClassification.TrustedInternal
        };

        var pending = state.Request(request, persistApprovalState: true, recovered: true);

        Assert.Equal(1, state.PendingCount);
        Assert.Equal(ApprovalTurnPhase.None, state.TurnPhase);
        Assert.Null(pending.TurnContext);
        Assert.Equal("legacy approval event is missing channel type", pending.TurnContextRestoreFailure);
        Assert.False(state.MarkRedriving(pending));
    }

    [Fact]
    public void Approval_turn_transitions_reject_invalid_source_states()
    {
        var state = new ToolApprovalState();
        var request = CreateRequest("call-1", requestedAtMs: 10);
        var pending = state.Request(request, persistApprovalState: true, recovered: true);

        Assert.True(state.MarkAbandoning());
        Assert.Equal(ApprovalTurnPhase.Abandoning, state.TurnPhase);
        Assert.False(state.MarkAbandoning());
        Assert.False(state.MarkRedriving(pending));

        state.ClearCalls();
        state.ClearTurn();
        pending = state.Request(request, persistApprovalState: true, recovered: true);
        Assert.True(state.Resolve(request.CallId, ApprovalDecision.ApprovedOnce, out _));
        Assert.True(state.MarkRedriving(pending));
        Assert.Equal(ApprovalTurnPhase.Redriving, state.TurnPhase);

        state.MarkRunningAfterRedrive();
        Assert.Equal(ApprovalTurnPhase.Running, state.TurnPhase);
    }

    [Fact]
    public void Latest_pending_request_uses_the_durable_request_time()
    {
        var state = new ToolApprovalState();
        state.Request(CreateRequest("newer", requestedAtMs: 20), persistApprovalState: true, recovered: true);
        state.Request(CreateRequest("older", requestedAtMs: 10), persistApprovalState: true, recovered: true);

        var latest = state.FindLatestPending(static _ => true);

        Assert.NotNull(latest);
        Assert.Equal("newer", latest.Request.CallId);
    }

    [Fact]
    public void Redrive_plan_uses_only_resolved_calls()
    {
        var state = new ToolApprovalState();
        var approved = CreateRequest("call-approved", requestedAtMs: 10);
        var denied = CreateRequest("call-denied", requestedAtMs: 20) with
        {
            SessionScratchDirectory = "/session/tmp"
        };
        var pending = CreateRequest("call-pending", requestedAtMs: 30);

        state.Request(approved, persistApprovalState: true, recovered: true);
        state.Request(denied, persistApprovalState: true, recovered: true);
        state.Request(pending, persistApprovalState: true, recovered: true);
        Assert.True(state.Resolve(approved.CallId, ApprovalDecision.ApprovedOnce, out _));
        Assert.True(state.Resolve(denied.CallId, ApprovalDecision.Denied, out _));

        var plan = state.BuildRedrivePlan([approved.CallId, denied.CallId, pending.CallId]);

        Assert.NotNull(plan.OneTimeApprovalPreSeed);
        Assert.NotNull(plan.DecisionOverride);
        Assert.NotNull(plan.SessionScratchDenialDirectories);
        Assert.NotNull(plan.AuthorizationAttemptIds);
        Assert.Equal(approved.Patterns, plan.OneTimeApprovalPreSeed[approved.CallId]);
        Assert.Equal(ApprovalDecision.Denied, plan.DecisionOverride[denied.CallId]);
        Assert.Equal("/session/tmp", plan.SessionScratchDenialDirectories[denied.CallId]);
        var attempts = plan.AuthorizationAttemptIds;
        Assert.Equal(approved.AuthorizationAttemptId, attempts[approved.CallId].Value);
        Assert.Equal(denied.AuthorizationAttemptId, attempts[denied.CallId].Value);
        Assert.False(attempts.ContainsKey(pending.CallId));
    }

    private static ToolApprovalRequested CreateRequest(string callId, long requestedAtMs)
    {
        var context = new TurnContext
        {
            SessionId = new SessionId("session-1"),
            TurnId = new TurnId("turn-1"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            ChannelType = ChannelType.Slack,
            RequesterSenderId = new SenderId("user-1"),
            RequesterPrincipal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
            SupportsInteractiveApproval = true
        };
        var authorizationAttemptId = AuthorizationAttemptId.New();

        return new ToolApprovalRequested
        {
            SessionId = context.SessionId,
            CallId = callId,
            AuthorizationAttemptId = authorizationAttemptId.Value,
            ToolName = "shell_execute",
            Patterns = ["git status"],
            CandidateVerbs = ["git"],
            Audience = context.Audience,
            Boundary = context.Boundary,
            ChannelType = context.ChannelType?.ToWireValue(),
            SupportsInteractiveApproval = context.SupportsInteractiveApproval,
            RequesterSenderId = context.RequesterSenderId,
            RequesterPrincipal = context.RequesterPrincipal,
            Cwd = "/repo",
            OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            TurnContext = context.ToRecord(),
            RequestedAtMs = requestedAtMs
        };
    }
}
