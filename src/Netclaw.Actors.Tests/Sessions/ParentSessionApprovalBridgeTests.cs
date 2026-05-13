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
        var bridge = new ParentSessionApprovalBridge(
            channel,
            request =>
            {
                emitted = request;
                channel.Complete(new ToolCallId(request.CallId), ApprovalDecision.ApprovedOnce);
            },
            new SessionId("signalr/thread-1"),
            requesterSenderId: "user-123",
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
            isMessy: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(ParentApprovalDecision.ApprovedOnce, decision);
        Assert.NotNull(emitted);
        Assert.Equal("user-123", emitted!.RequesterSenderId);
        Assert.Equal(PrincipalClassification.Operator, emitted.RequesterPrincipal);
        Assert.True(emitted.HasAdoptedContext);
        Assert.True(emitted.HasThirdPartyAdoptedContext);
        Assert.True(emitted.PersistedAdoptedContext);
        Assert.Equal(["user-123", "user-456"], emitted.AdoptedSpeakerIds);
        Assert.Equal(["grep timeout logs/app.log | wc -l"], emitted.Patterns);
        Assert.Equal(["grep timeout logs/app.log"], emitted.CandidateVerbs);
        Assert.Equal(ApprovalOptionKeys.ApproveSessionLabel, emitted.Options.Single(o => o.Key == ApprovalOptionKeys.ApproveSession).Label);
        Assert.Equal(ApprovalOptionKeys.ApproveAlwaysLabel, emitted.Options.Single(o => o.Key == ApprovalOptionKeys.ApproveAlways).Label);
    }

    [Fact]
    public async Task Bridge_preserves_self_only_adopted_context_without_third_party_flag()
    {
        var channel = new ApprovalChannel();
        ToolInteractionRequest? emitted = null;
        var bridge = new ParentSessionApprovalBridge(
            channel,
            request =>
            {
                emitted = request;
                channel.Complete(new ToolCallId(request.CallId), ApprovalDecision.ApprovedOnce);
            },
            new SessionId("signalr/thread-2"),
            requesterSenderId: "user-123",
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
            isMessy: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(ParentApprovalDecision.ApprovedOnce, decision);
        Assert.NotNull(emitted);
        Assert.True(emitted!.HasAdoptedContext);
        Assert.False(emitted.HasThirdPartyAdoptedContext);
        Assert.Equal(["user-123"], emitted.AdoptedSpeakerIds);
    }
}
