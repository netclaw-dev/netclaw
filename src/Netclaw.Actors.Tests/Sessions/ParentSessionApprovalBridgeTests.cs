// -----------------------------------------------------------------------
// <copyright file="ParentSessionApprovalBridgeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ParentSessionApprovalBridgeTests
{
    private static readonly ToolExecutionTimeout ApprovalTimeout = new(TimeSpan.FromMinutes(5));

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
                channel.Complete(dispatch.Request.CallId, ConsentAnswer.Once.Instance);
            },
            ApprovalTimeout,
            new SessionId("signalr/thread-1"),
            approvalScopeId: "spawn-call-1",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: true,
            hasThirdPartyAdoptedContext: true,
            adoptedSpeakerIds: ["user-123", "user-456"]);

        var authorizationAttemptId = AuthorizationAttemptId.New();
        var assignmentDigest = new ApprovalAssignmentDigest($"sha256:{new string('a', 64)}");
        var decision = await bridge.RequestConsentAsync(
            new ParentApprovalRequest(
                authorizationAttemptId,
                new ToolCallId("call-1"),
                "shell_execute",
                new ToolApprovalContext(
                    "shell_execute",
                    "grep timeout logs/app.log | wc -l",
                    ["grep timeout logs/app.log | wc -l"],
                    ["grep timeout logs/app.log"],
                    [
                        new ToolApprovalOption(new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce), ApprovalOptionKeys.ApproveOnceLabel),
                        new ToolApprovalOption(new ApprovalOptionKey(ApprovalOptionKeys.ApproveSession), ApprovalOptionKeys.ApproveSessionLabel),
                        new ToolApprovalOption(new ApprovalOptionKey(ApprovalOptionKeys.ApproveAlways), ApprovalOptionKeys.ApproveAlwaysLabel),
                        new ToolApprovalOption(new ApprovalOptionKey(ApprovalOptionKeys.ApproveEverywhere), ApprovalOptionKeys.ApproveEverywhereLabel),
                        new ToolApprovalOption(new ApprovalOptionKey(ApprovalOptionKeys.Deny), ApprovalOptionKeys.DenyLabel),
                    ],
                    Cwd: "/home/user/repos/foo",
                    Candidates:
                    [
                        new ApprovalCandidate(
                            "grep",
                            "/home/user/repos/foo")
                        {
                            AssignmentDigest = assignmentDigest,
                            Shell = ApprovalShell.Bash,
                            VerbTokens = Array.AsReadOnly(["grep", "timeout"]),
                        }
                    ])),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConsentAnswer.Once.Instance, decision.Answer);
        Assert.NotNull(emitted);
        Assert.Equal(authorizationAttemptId.Value, emitted!.AuthorizationAttemptId);
        Assert.Equal("user-123", emitted.RequesterSenderId?.Value);
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
        Assert.Equal(ApprovalShell.Bash, emitted.Candidates[0].Shell);
        Assert.Equal(["grep", "timeout"], emitted.Candidates[0].VerbTokens);
        Assert.Equal(assignmentDigest, emitted.Candidates[0].AssignmentDigest);
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
                channel.Complete(dispatch.Request.CallId, ConsentAnswer.Once.Instance);
            },
            ApprovalTimeout,
            new SessionId("signalr/thread-2"),
            approvalScopeId: "spawn-call-2",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: true,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: ["user-123"]);

        var decision = await bridge.RequestConsentAsync(
            ShellRequest(new ToolCallId("call-2"), "cat logs/app.log", null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConsentAnswer.Once.Instance, decision.Answer);
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
            ApprovalTimeout,
            new SessionId("signalr/thread-missing-sender"),
            approvalScopeId: "spawn-call-missing-sender",
            requesterSenderId: null,
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        await Assert.ThrowsAsync<ParentApprovalUnavailableException>(() => bridge.RequestConsentAsync(
            ShellRequest(callId, "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo"),
            TestContext.Current.CancellationToken));

        Assert.False(emitted);
        Assert.False(channel.Complete(callId, ConsentAnswer.Once.Instance));
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
            ApprovalTimeout,
            new SessionId("signalr/thread-missing-principal"),
            approvalScopeId: "spawn-call-missing-principal",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: null,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        await Assert.ThrowsAsync<ParentApprovalUnavailableException>(() => bridge.RequestConsentAsync(
            ShellRequest(callId, "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo"),
            TestContext.Current.CancellationToken));

        Assert.False(emitted);
        Assert.False(channel.Complete(callId, ConsentAnswer.Once.Instance));
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
                channel.Complete(dispatch.Request.CallId, ConsentAnswer.Once.Instance);
            },
            ApprovalTimeout,
            new SessionId("reminder/thread-automation"),
            approvalScopeId: "spawn-call-automation",
            requesterSenderId: null,
            requesterPrincipal: PrincipalClassification.VerifiedAutomation,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        var decision = await bridge.RequestConsentAsync(
            ShellRequest(new ToolCallId("call-automation"), "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConsentAnswer.Once.Instance, decision.Answer);
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
                        channel.Complete(emitted[0].Request.CallId, ConsentAnswer.Once.Instance);
                        channel.Complete(emitted[1].Request.CallId, ConsentAnswer.Denied);
                    }
                }
            },
            ApprovalTimeout,
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

        Assert.Equal([ConsentAnswer.Once.Instance, ConsentAnswer.Denied], decisions.Select(decision => decision.Answer));
        Assert.NotNull(decisions[0].RetryConsent);
        Assert.Null(decisions[1].RetryConsent);
        Assert.Equal(2, emitted.Count);
        Assert.NotEqual(emitted[0].Request.CallId, emitted[1].Request.CallId);
        Assert.All(emitted, dispatch =>
        {
            Assert.False(dispatch.PersistApprovalState);
            Assert.NotEqual(childCallId, dispatch.Request.CallId);
            Assert.StartsWith("spawn-call-duplicates/subagent-approval/", dispatch.Request.CallId.Value, StringComparison.Ordinal);
            Assert.DoesNotContain(childCallId.Value, dispatch.Request.CallId.Value, StringComparison.Ordinal);
        });
        Assert.False(channel.Complete(childCallId, ConsentAnswer.Once.Instance));
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
            ApprovalTimeout,
            new SessionId("signalr/thread-late"),
            approvalScopeId: "spawn-call-late",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        var waitTask = bridge.RequestConsentAsync(
            ShellRequest(callId, "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo"),
            cts.Token);
        Assert.NotNull(emitted);

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waitTask);

        Assert.False(channel.Complete(emitted!.CallId, ConsentAnswer.Once.Instance));
        var lateWaitResult = await channel.WaitForApprovalAsync(
            callId,
            TimeSpan.FromMilliseconds(25),
            TestContext.Current.CancellationToken);
        Assert.Equal(ConsentAnswer.TimedOut, lateWaitResult);
    }

    // A sub-agent consent wait uses the session's approval timeout, as a session
    // consent wait does. The daemon gives an infinite timeout to both.
    [Fact]
    public async Task Sub_agent_wait_ends_at_the_session_approval_timeout()
    {
        var channel = new ApprovalChannel();
        ToolInteractionRequest? emitted = null;
        var bridge = new ParentSessionApprovalBridge(
            channel,
            dispatch => emitted = dispatch.Request,
            new ToolExecutionTimeout(TimeSpan.FromMilliseconds(50)),
            new SessionId("signalr/thread-timeout"),
            approvalScopeId: "spawn-call-timeout",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        var step = await bridge.RequestConsentAsync(
            ShellRequest(new ToolCallId("call-timeout"), "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConsentAnswer.TimedOut, step.Answer);
        Assert.Null(step.RetryConsent);
        Assert.False(channel.Complete(emitted!.CallId, ConsentAnswer.Once.Instance));
    }

    [Fact]
    public async Task Session_call_prompt_uses_the_call_id_and_journals_the_wait()
    {
        var channel = new ApprovalChannel();
        ToolInteractionRequestDispatch? emitted = null;
        var bridge = new ParentSessionApprovalBridge(
            channel,
            dispatch =>
            {
                emitted = dispatch;
                channel.Complete(dispatch.Request.CallId, new ConsentAnswer.Grant(GrantScopeKind.Session));
            },
            ApprovalTimeout,
            new SessionId("signalr/thread-session-call"),
            approvalScopeId: "call-session",
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);
        var request = ShellRequest(new ToolCallId("call-session"), "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo");
        request = request with
        {
            Approval = request.Approval with { IsManagedTemporaryRetry = true, ManagedTemporaryDirectory = "/home/user/.netclaw/tmp" }
        };

        var step = await bridge.RequestSessionConsentAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(emitted);
        Assert.True(emitted!.PersistApprovalState);
        Assert.Equal("call-session", emitted.Request.CallId.Value);
        Assert.Equal("/home/user/.netclaw/tmp", emitted.ManagedTemporaryDirectory);
        // A session grant still seeds the exact one-time consent for the retry.
        Assert.Equal(OneTimeApprovalKeys.CreateConsent("shell_execute", request.Approval), step.RetryConsent);
    }

    private static Task<ConsentStep> RequestShellApprovalAsync(
        ParentSessionApprovalBridge bridge,
        ToolCallId callId)
        => bridge.RequestConsentAsync(
            ShellRequest(callId, "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo"),
            TestContext.Current.CancellationToken);

    private static ParentApprovalRequest ShellRequest(
        ToolCallId callId,
        string command,
        string? cwd,
        string? candidateDirectory)
        => new(
            AuthorizationAttemptId.New(),
            callId,
            "shell_execute",
            new ToolApprovalContext(
                "shell_execute",
                command,
                [command],
                [command],
                [new ToolApprovalOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel)],
                Cwd: cwd,
                IsMessy: false,
                Candidates: [new ApprovalCandidate(command, candidateDirectory)]));
}
