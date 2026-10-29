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

    // The owner of a parked run builds each child prompt with ForChildCall (durable-child-consent-wait, design D4).
    [Fact]
    public void Child_prompt_preserves_requester_identity_and_adopted_context()
    {
        var builder = new ApprovalRequestBuilder(
            new SessionId("signalr/thread-1"),
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: true,
            hasThirdPartyAdoptedContext: true,
            adoptedSpeakerIds: ["user-123", "user-456"]);

        var authorizationAttemptId = AuthorizationAttemptId.New();
        var assignmentDigest = new ApprovalAssignmentDigest($"sha256:{new string('a', 64)}");
        var dispatch = builder.ForChildCall(ChildApprovalPrompt.CreateCallId("spawn-call-1", 1),
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
                    ])));

        var emitted = dispatch.Request;
        Assert.Equal(authorizationAttemptId.Value, emitted.AuthorizationAttemptId);
        Assert.Equal("user-123", emitted.RequesterSenderId?.Value);
        Assert.Equal(PrincipalClassification.Operator, emitted.RequesterPrincipal);
        Assert.True(emitted.HasAdoptedContext);
        Assert.True(emitted.HasThirdPartyAdoptedContext);
        Assert.True(emitted.PersistedAdoptedContext);
        Assert.Equal(new ToolCallId("call-1"), dispatch.OriginalChildCallId);
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
    public void Child_prompt_preserves_self_only_adopted_context_without_third_party_flag()
    {
        var builder = new ApprovalRequestBuilder(
            new SessionId("signalr/thread-2"),
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: true,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: ["user-123"]);

        var emitted = builder.ForChildCall(ChildApprovalPrompt.CreateCallId("spawn-call-2", 1),
            ShellRequest(new ToolCallId("call-2"), "cat logs/app.log", null, null)).Request;

        Assert.True(emitted.HasAdoptedContext);
        Assert.False(emitted.HasThirdPartyAdoptedContext);
        Assert.Equal(["user-123"], emitted.AdoptedSpeakerIds);
    }

    [Fact]
    public void Child_prompt_without_human_requester_sender_fails()
    {
        var builder = new ApprovalRequestBuilder(
            new SessionId("signalr/thread-missing-sender"),
            requesterSenderId: null,
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        Assert.Throws<ParentApprovalUnavailableException>(() => builder.ForChildCall(
            ChildApprovalPrompt.CreateCallId("spawn-call-missing-sender", 1),
            ShellRequest(new ToolCallId("call-missing-sender"), "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo")));
    }

    [Fact]
    public void Child_prompt_without_requester_principal_fails()
    {
        var builder = new ApprovalRequestBuilder(
            new SessionId("signalr/thread-missing-principal"),
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: null,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        Assert.Throws<ParentApprovalUnavailableException>(() => builder.ForChildCall(
            ChildApprovalPrompt.CreateCallId("spawn-call-missing-principal", 1),
            ShellRequest(new ToolCallId("call-missing-principal"), "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo")));
    }

    [Fact]
    public void Verified_automation_child_prompt_allows_missing_sender()
    {
        var builder = new ApprovalRequestBuilder(
            new SessionId("reminder/thread-automation"),
            requesterSenderId: null,
            requesterPrincipal: PrincipalClassification.VerifiedAutomation,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        var emitted = builder.ForChildCall(ChildApprovalPrompt.CreateCallId("spawn-call-automation", 1),
            ShellRequest(new ToolCallId("call-automation"), "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo")).Request;

        Assert.Null(emitted.RequesterSenderId);
        Assert.Equal(PrincipalClassification.VerifiedAutomation, emitted.RequesterPrincipal);
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
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        var waitTask = bridge.RequestSessionConsentAsync(
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

    // A session consent wait ends at the session approval timeout. The daemon gives an infinite timeout.
    [Fact]
    public async Task Session_wait_ends_at_the_session_approval_timeout()
    {
        var channel = new ApprovalChannel();
        ToolInteractionRequest? emitted = null;
        var bridge = new ParentSessionApprovalBridge(
            channel,
            dispatch => emitted = dispatch.Request,
            new ToolExecutionTimeout(TimeSpan.FromMilliseconds(50)),
            new SessionId("signalr/thread-timeout"),
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);

        var step = await bridge.RequestSessionConsentAsync(
            ShellRequest(new ToolCallId("call-timeout"), "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConsentAnswer.TimedOut, step.Answer);
        Assert.Null(step.RetryConsent);
        Assert.False(channel.Complete(emitted!.CallId, ConsentAnswer.Once.Instance));
    }

    [Fact]
    public async Task Session_call_prompt_uses_the_call_id_and_keeps_the_managed_temporary_directory()
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
        Assert.Equal("call-session", emitted!.Request.CallId.Value);
        Assert.Equal("/home/user/.netclaw/tmp", emitted.ManagedTemporaryDirectory);
        // A session grant still seeds the exact one-time consent for the retry.
        Assert.Equal(OneTimeApprovalKeys.CreateConsent("shell_execute", request.Approval), step.RetryConsent);
    }

    /// <summary>
    /// durable-child-consent-wait, task 2.1. The shape of a child prompt. The prompt keeps the original child call ID
    /// for the journal. It never carries the managed temporary directory, because the retry target of a parked child
    /// call is deferred to a later PR. The gate facts go to the prompt without change. ChildParkSpecs proves that the
    /// owner writes each child prompt to the journal.
    /// </summary>
    [Fact]
    public void A_child_prompt_names_its_child_call_and_leaves_out_the_managed_temporary_directory()
    {
        var builder = new ApprovalRequestBuilder(
            new SessionId("signalr/thread-child-shape"),
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);
        var request = ShellRequest(new ToolCallId("child-call-7"), "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo");
        request = request with
        {
            Approval = request.Approval with
            {
                IsMessy = true,
                IsManagedTemporaryRetry = true,
                ManagedTemporaryDirectory = "/home/user/.netclaw/tmp"
            }
        };

        var emitted = builder.ForChildCall(ChildApprovalPrompt.CreateCallId("spawn-call-shape", 1), request);

        Assert.Equal(new ToolCallId("child-call-7"), emitted.OriginalChildCallId);
        Assert.Null(emitted.ManagedTemporaryDirectory);
        Assert.Equal("approval", emitted.Request.Kind);
        Assert.Equal(new ToolName("shell_execute"), emitted.Request.ToolName);
        Assert.Equal(request.AuthorizationAttemptId.Value, emitted.Request.AuthorizationAttemptId);
        Assert.Equal("git push origin main", emitted.Request.DisplayText);
        Assert.True(emitted.Request.IsMessy);
        Assert.Equal("/home/user/repos/foo", emitted.Request.Cwd);
        Assert.Null(emitted.Request.RepositoryCommonDirectory);
    }

    /// <summary>
    /// The grant-scope notes of an approval context travel to the prompt in their own field, so a channel can show
    /// them beside the command (#2746). The command text stays the display text.
    /// </summary>
    [Fact]
    public void A_prompt_carries_the_approval_notes_apart_from_the_command()
    {
        var builder = new ApprovalRequestBuilder(
            new SessionId("signalr/thread-notes"),
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);
        var request = ShellRequest(new ToolCallId("call-notes"), "git push origin main", "/home/user/repos/foo", "/home/user/repos/foo");
        request = request with { Approval = request.Approval with { Notes = "Approve repository covers git commands." } };

        var emitted = builder.ForChildCall(ChildApprovalPrompt.CreateCallId("spawn-call-notes", 1), request);

        Assert.Equal("Approve repository covers git commands.", emitted.Request.Notes);
        Assert.Equal("git push origin main", emitted.Request.DisplayText);
    }

    /// <summary>
    /// durable-child-consent-wait, task 2.1. A characterization of the option filter before the move to the shared
    /// prompt builder. A repository option needs a repository directory: without one, the prompt drops each
    /// repository variant and keeps the other options in order. A session prompt has no child call ID.
    /// </summary>
    [Fact]
    public async Task The_repository_options_appear_only_with_a_repository_directory()
    {
        var channel = new ApprovalChannel();
        var emitted = new List<ToolInteractionRequestDispatch>();
        var bridge = new ParentSessionApprovalBridge(
            channel,
            dispatch =>
            {
                emitted.Add(dispatch);
                channel.Complete(dispatch.Request.CallId, ConsentAnswer.Once.Instance);
            },
            ApprovalTimeout,
            new SessionId("signalr/thread-repository-options"),
            requesterSenderId: new SenderId("user-123"),
            requesterPrincipal: PrincipalClassification.Operator,
            hasAdoptedContext: false,
            hasThirdPartyAdoptedContext: false,
            adoptedSpeakerIds: []);
        ToolApprovalOption[] options =
        [
            new(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new(ApprovalOptionKeys.ApproveRepositoryKey, ApprovalOptionKeys.ApproveRepositoryLabel),
            new(ApprovalOptionKeys.ApproveAssignmentRepositoryV1Key, ApprovalOptionKeys.ApproveRepositoryLabel),
            new(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        ];
        var outside = ShellRequest(new ToolCallId("call-outside"), "git status", "/home/user/scratch", "/home/user/scratch");
        outside = outside with { Approval = outside.Approval with { Options = options } };
        var inside = ShellRequest(new ToolCallId("call-inside"), "git status", "/home/user/repos/foo", "/home/user/repos/foo");
        inside = inside with
        {
            Approval = inside.Approval with { Options = options, RepositoryCommonDirectory = "/home/user/repos/foo/.git" }
        };

        await bridge.RequestSessionConsentAsync(outside, TestContext.Current.CancellationToken);
        await bridge.RequestSessionConsentAsync(inside, TestContext.Current.CancellationToken);

        Assert.Equal(2, emitted.Count);
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            emitted[0].Request.Options.Select(static option => option.Key.Value));
        Assert.Null(emitted[0].Request.RepositoryCommonDirectory);
        Assert.Equal(
            [
                ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveRepository,
                ApprovalOptionKeys.ApproveAssignmentRepositoryV1, ApprovalOptionKeys.Deny
            ],
            emitted[1].Request.Options.Select(static option => option.Key.Value));
        Assert.Equal("/home/user/repos/foo/.git", emitted[1].Request.RepositoryCommonDirectory);
        Assert.All(emitted, static dispatch => Assert.Null(dispatch.OriginalChildCallId));
    }

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
