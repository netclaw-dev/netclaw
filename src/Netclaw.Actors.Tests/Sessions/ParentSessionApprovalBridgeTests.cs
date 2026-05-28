// -----------------------------------------------------------------------
// <copyright file="ParentSessionApprovalBridgeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ParentSessionApprovalBridgeTests
{
    [Fact]
    public async Task Bridge_preserves_requester_identity_and_adopted_context()
    {
        var channel = new ApprovalChannel();
        ToolInteractionRequest? emitted = null;
        bool? persistApprovalState = null;
        var bridge = new ParentSessionApprovalBridge(
            channel,
            dispatch =>
            {
                emitted = dispatch.Request;
                persistApprovalState = dispatch.PersistApprovalState;
                channel.Complete(dispatch.Request.CallId, ApprovalDecision.ApprovedOnce);
            },
            new SessionId("signalr/thread-1"),
            approvalScopeId: "spawn-call-1",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: true,
            hasThirdPartyAdoptedContext: true,
            adoptedSpeakerIds: ["user-123", "user-456"]);

        var decision = await bridge.RequestApprovalAsync(
            new ToolCallId("call-1"),
            "shell_execute",
            "grep timeout logs/app.log | wc -l",
            ["grep timeout logs/app.log | wc -l"],
            ["grep timeout logs/app.log"],
            [new ParentApprovalCandidate("grep timeout logs/app.log", "/home/user/repos/foo")],
            "/home/user/repos/foo",
            [
                new ParentApprovalOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
                new ParentApprovalOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
                new ParentApprovalOption(ApprovalOptionKeys.ApproveAlways, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ParentApprovalOption(ApprovalOptionKeys.ApproveEverywhere, ApprovalOptionKeys.ApproveEverywhereLabel),
                new ParentApprovalOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel),
            ],
            isMessy: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(ParentApprovalDecision.ApprovedOnce, decision);
        Assert.NotNull(emitted);
        Assert.Equal("user-123", emitted!.RequesterSenderId?.Value);
        Assert.Equal(PrincipalClassification.Operator, emitted.RequesterPrincipal);
        Assert.True(emitted.HasAdoptedContext);
        Assert.True(emitted.HasThirdPartyAdoptedContext);
        Assert.True(emitted.PersistedAdoptedContext);
        Assert.False(persistApprovalState);
        Assert.NotEqual("call-1", emitted.CallId.Value);
        Assert.Contains("call-1", emitted.CallId.Value, StringComparison.Ordinal);
        Assert.Equal(["user-123", "user-456"], emitted.AdoptedSpeakerIds);
        Assert.Equal(["grep timeout logs/app.log | wc -l"], emitted.Patterns);
        Assert.Equal(["grep timeout logs/app.log"], emitted.CandidateVerbs);
        Assert.Equal("/home/user/repos/foo", emitted.Cwd);
        Assert.Single(emitted.Candidates);
        Assert.Equal("/home/user/repos/foo", emitted.Candidates[0].Directory);
        Assert.Equal(ApprovalOptionKeys.ApproveSessionLabel, emitted.Options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveSession).Label);
        Assert.Equal(ApprovalOptionKeys.ApproveAlwaysLabel, emitted.Options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveAlways).Label);
        Assert.Equal(ApprovalOptionKeys.ApproveEverywhereLabel, emitted.Options.Single(o => o.Key.Value == ApprovalOptionKeys.ApproveEverywhere).Label);
    }

    [Fact]
    public async Task Bridge_preserves_self_only_adopted_context_without_third_party_flag()
    {
        var channel = new ApprovalChannel();
        ToolInteractionRequest? emitted = null;
        var bridge = new ParentSessionApprovalBridge(
            channel,
            dispatch =>
            {
                emitted = dispatch.Request;
                channel.Complete(dispatch.Request.CallId, ApprovalDecision.ApprovedOnce);
            },
            new SessionId("signalr/thread-2"),
            approvalScopeId: "spawn-call-2",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: true,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: ["user-123"]);

        var decision = await bridge.RequestApprovalAsync(
            new ToolCallId("call-2"),
            "shell_execute",
            "cat logs/app.log",
            ["cat logs/app.log"],
            ["cat logs/app.log"],
            [new ParentApprovalCandidate("cat logs/app.log", null)],
            cwd: null,
            [new ParentApprovalOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel)],
            isMessy: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(ParentApprovalDecision.ApprovedOnce, decision);
        Assert.NotNull(emitted);
        Assert.True(emitted!.HasAdoptedContext);
        Assert.False(emitted.HasThirdPartyAdoptedContext);
        Assert.Equal(["user-123"], emitted.AdoptedSpeakerIds);
    }

    [Fact]
    public async Task Bridge_without_human_requester_sender_fails_without_emitting_prompt()
    {
        var channel = new ApprovalChannel();
        var emitted = false;
        var callId = new ToolCallId("call-missing-sender");
        var bridge = new ParentSessionApprovalBridge(
            channel,
            _ => emitted = true,
            new SessionId("signalr/thread-missing-sender"),
            approvalScopeId: "spawn-call-missing-sender",
            requesterSenderId: null,
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        await Assert.ThrowsAsync<ParentApprovalUnavailableException>(() => bridge.RequestApprovalAsync(
            callId,
            "shell_execute",
            "git push origin main",
            ["git push origin main"],
            ["git push origin main"],
            [new ParentApprovalCandidate("git push origin main", "/home/user/repos/foo")],
            "/home/user/repos/foo",
            [new ParentApprovalOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel)],
            isMessy: false,
            TestContext.Current.CancellationToken));

        Assert.False(emitted);
        Assert.False(channel.Complete(callId, ApprovalDecision.ApprovedOnce));
    }

    [Fact]
    public async Task Bridge_without_requester_principal_fails_without_emitting_prompt()
    {
        var channel = new ApprovalChannel();
        var emitted = false;
        var callId = new ToolCallId("call-missing-principal");
        var bridge = new ParentSessionApprovalBridge(
            channel,
            _ => emitted = true,
            new SessionId("signalr/thread-missing-principal"),
            approvalScopeId: "spawn-call-missing-principal",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: null,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        await Assert.ThrowsAsync<ParentApprovalUnavailableException>(() => bridge.RequestApprovalAsync(
            callId,
            "shell_execute",
            "git push origin main",
            ["git push origin main"],
            ["git push origin main"],
            [new ParentApprovalCandidate("git push origin main", "/home/user/repos/foo")],
            "/home/user/repos/foo",
            [new ParentApprovalOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel)],
            isMessy: false,
            TestContext.Current.CancellationToken));

        Assert.False(emitted);
        Assert.False(channel.Complete(callId, ApprovalDecision.ApprovedOnce));
    }

    [Fact]
    public async Task Verified_automation_bridge_allows_missing_sender()
    {
        var channel = new ApprovalChannel();
        ToolInteractionRequest? emitted = null;
        var bridge = new ParentSessionApprovalBridge(
            channel,
            dispatch =>
            {
                emitted = dispatch.Request;
                channel.Complete(dispatch.Request.CallId, ApprovalDecision.ApprovedOnce);
            },
            new SessionId("reminder/thread-automation"),
            approvalScopeId: "spawn-call-automation",
            requesterSenderId: null,
            requesterPrincipal: PrincipalClassification.VerifiedAutomation,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        var decision = await bridge.RequestApprovalAsync(
            new ToolCallId("call-automation"),
            "shell_execute",
            "git push origin main",
            ["git push origin main"],
            ["git push origin main"],
            [new ParentApprovalCandidate("git push origin main", "/home/user/repos/foo")],
            "/home/user/repos/foo",
            [new ParentApprovalOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel)],
            isMessy: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(ParentApprovalDecision.ApprovedOnce, decision);
        Assert.NotNull(emitted);
        Assert.Null(emitted!.RequesterSenderId);
        Assert.Equal(PrincipalClassification.VerifiedAutomation, emitted.RequesterPrincipal);
    }

    [Fact]
    public async Task Bridge_namespaces_duplicate_child_call_ids_per_request()
    {
        var channel = new ApprovalChannel();
        var emitted = new List<ToolInteractionRequestDispatch>();
        var gate = new object();
        var childCallId = new ToolCallId("call-duplicate-child");
        var bridge = new ParentSessionApprovalBridge(
            channel,
            dispatch =>
            {
                lock (gate)
                {
                    emitted.Add(dispatch);
                    if (emitted.Count == 2)
                    {
                        channel.Complete(emitted[0].Request.CallId, ApprovalDecision.ApprovedOnce);
                        channel.Complete(emitted[1].Request.CallId, ApprovalDecision.Denied);
                    }
                }
            },
            new SessionId("signalr/thread-duplicates"),
            approvalScopeId: "spawn-call-duplicates",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        var first = RequestShellApprovalAsync(bridge, childCallId);
        var second = RequestShellApprovalAsync(bridge, childCallId);

        var decisions = await Task.WhenAll(first, second).WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);

        Assert.Equal([ParentApprovalDecision.ApprovedOnce, ParentApprovalDecision.Denied], decisions);
        Assert.Equal(2, emitted.Count);
        Assert.NotEqual(emitted[0].Request.CallId, emitted[1].Request.CallId);
        Assert.All(emitted, dispatch =>
        {
            Assert.False(dispatch.PersistApprovalState);
            Assert.NotEqual(childCallId, dispatch.Request.CallId);
            Assert.Contains(childCallId.Value, dispatch.Request.CallId.Value, StringComparison.Ordinal);
        });
        Assert.False(channel.Complete(childCallId, ApprovalDecision.ApprovedOnce));
    }

    [Fact]
    public async Task Cancelled_bridge_wait_ignores_late_approval_response()
    {
        var channel = new ApprovalChannel();
        ToolInteractionRequest? emitted = null;
        var callId = new ToolCallId("call-late");
        using var cts = new CancellationTokenSource();
        var bridge = new ParentSessionApprovalBridge(
            channel,
            dispatch => emitted = dispatch.Request,
            new SessionId("signalr/thread-late"),
            approvalScopeId: "spawn-call-late",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        var waitTask = bridge.RequestApprovalAsync(
            callId,
            "shell_execute",
            "git push origin main",
            ["git push origin main"],
            ["git push origin main"],
            [new ParentApprovalCandidate("git push origin main", "/home/user/repos/foo")],
            "/home/user/repos/foo",
            [new ParentApprovalOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel)],
            isMessy: false,
            cts.Token);
        Assert.NotNull(emitted);

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waitTask);

        Assert.False(channel.Complete(emitted!.CallId, ApprovalDecision.ApprovedOnce));
        var lateWaitResult = await channel.WaitForApprovalAsync(
            callId,
            TimeSpan.FromMilliseconds(25),
            TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalDecision.TimedOut, lateWaitResult);
    }

    private static Task<ParentApprovalDecision> RequestShellApprovalAsync(
        ParentSessionApprovalBridge bridge,
        ToolCallId callId)
        => bridge.RequestApprovalAsync(
            callId,
            "shell_execute",
            "git push origin main",
            ["git push origin main"],
            ["git push origin main"],
            [new ParentApprovalCandidate("git push origin main", "/home/user/repos/foo")],
            "/home/user/repos/foo",
            [new ParentApprovalOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel)],
            isMessy: false,
            TestContext.Current.CancellationToken);
}
