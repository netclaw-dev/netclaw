// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
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
    public void Restart_stop_requires_an_unresolved_durable_prompt_with_restored_authority()
    {
        var state = new ToolApprovalState();
        var request = CreateRequest("call-1", requestedAtMs: 10);

        state.Request(request, persistApprovalState: false, recovered: false);
        Assert.False(state.HasRecoverablePending(request.CallId));

        state.Request(request, persistApprovalState: true, recovered: false);
        Assert.True(state.HasRecoverablePending(request.CallId));

        Assert.True(state.Resolve(request.CallId, ApprovalDecision.ApprovedOnce, out _));
        Assert.False(state.HasRecoverablePending(request.CallId));

        var legacy = request with { CallId = "legacy-restorable", TurnContext = null };
        state.Request(legacy, persistApprovalState: true, recovered: true);
        Assert.False(state.HasRecoverablePending(legacy.CallId));

        var incomplete = request with { CallId = "legacy-call", TurnContext = null, ChannelType = null };
        state.Request(incomplete, persistApprovalState: true, recovered: true);
        Assert.False(state.HasRecoverablePending(incomplete.CallId));
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
    public void A_legacy_prompt_cannot_authorize_a_new_repository_scope()
    {
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [], ApprovalOptionKeys.ApproveRepository, "/work/main/.git"));
        Assert.True(LlmSessionActor.IsOfferedApprovalOption(
            [], ApprovalOptionKeys.ApproveOnce, repositoryCommonDirectory: null));
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            ApprovalOptionKeys.ApproveRepository,
            "/work/main/.git"));
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveRepository],
            ApprovalOptionKeys.ApproveRepository,
            repositoryCommonDirectory: null));
        Assert.True(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveRepository],
            ApprovalOptionKeys.ApproveRepository,
            "/work/main/.git"));
    }

    [Fact]
    public void Assignment_repository_option_requires_its_exact_offered_key()
    {
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveRepository],
            ApprovalOptionKeys.ApproveAssignmentRepositoryV1,
            "/work/main/.git"));
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveAssignmentRepositoryV1],
            ApprovalOptionKeys.ApproveRepository,
            "/work/main/.git"));
        Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveAssignmentRepositoryV1],
            ApprovalOptionKeys.ApproveAssignmentRepositoryV1,
            repositoryCommonDirectory: null));
        Assert.True(LlmSessionActor.IsOfferedApprovalOption(
            [ApprovalOptionKeys.ApproveAssignmentRepositoryV1],
            ApprovalOptionKeys.ApproveAssignmentRepositoryV1,
            "/work/main/.git"));
    }

    [Theory]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentSessionV1, ApprovalDecision.ApprovedSession)]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentAlwaysV1, ApprovalDecision.ApprovedAlways)]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentRepositoryV1, ApprovalDecision.ApprovedRepository)]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentEverywhereV1, ApprovalDecision.ApprovedEverywhere)]
    public void New_runtime_maps_assignment_option_keys(
        string optionKey,
        ApprovalDecision expected)
        => Assert.Equal(expected, LlmSessionActor.MapApprovalDecision(optionKey));

    [Theory]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentSessionV1)]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentAlwaysV1)]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentRepositoryV1)]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentEverywhereV1)]
    public void Legacy_prompt_rejects_each_assignment_option_key(string optionKey)
        => Assert.False(LlmSessionActor.IsOfferedApprovalOption(
            [],
            optionKey,
            "/work/repository/.git"));

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
            ManagedTemporaryDirectory = "/session/tmp"
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
        Assert.NotNull(plan.ManagedTemporaryDenialDirectories);
        Assert.NotNull(plan.AuthorizationAttemptIds);
        Assert.Equal(approved.Patterns, plan.OneTimeApprovalPreSeed[approved.CallId]);
        Assert.Equal(ApprovalDecision.Denied, plan.DecisionOverride[denied.CallId]);
        Assert.Equal("/session/tmp", plan.ManagedTemporaryDenialDirectories[denied.CallId]);
        var attempts = plan.AuthorizationAttemptIds;
        Assert.Equal(approved.AuthorizationAttemptId, attempts[approved.CallId].Value);
        Assert.Equal(denied.AuthorizationAttemptId, attempts[denied.CallId].Value);
        Assert.False(attempts.ContainsKey(pending.CallId));
    }

    [Fact]
    public void Resolved_assignment_grant_redrives_only_the_exact_call_once()
    {
        var digest = new ApprovalAssignmentDigest($"sha256:{new string('a', 64)}");
        var candidate = new ApprovalCandidate(
            "inspect",
            "/work/repository",
            ApprovalAssignmentConstraint.ExactDigest(digest))
        {
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
        var state = new ToolApprovalState();
        state.Request(request, persistApprovalState: true, recovered: true);
        Assert.True(state.Resolve(request.CallId, ApprovalDecision.ApprovedAlways, out _));

        var plan = state.BuildRedrivePlan([request.CallId]);

        Assert.Equal(
            OneTimeApprovalKeys.Create(request.Patterns, request.Candidates, request.Cwd),
            plan.OneTimeApprovalPreSeed![request.CallId]);
        Assert.Null(plan.DecisionOverride);
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
