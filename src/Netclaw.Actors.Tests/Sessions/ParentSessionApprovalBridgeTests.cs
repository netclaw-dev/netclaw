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
            adoptedSpeakerIds: ["user-123", "user-456"]);

        var decision = await bridge.RequestApprovalAsync(
            new ToolCallId("call-1"),
            "shell_execute",
            "git push origin main",
            ["git push"],
            TestContext.Current.CancellationToken);

        Assert.Equal(ParentApprovalDecision.ApprovedOnce, decision);
        Assert.NotNull(emitted);
        Assert.Equal("user-123", emitted!.RequesterSenderId);
        Assert.Equal(PrincipalClassification.Operator, emitted.RequesterPrincipal);
        Assert.True(emitted.HasAdoptedContext);
        Assert.True(emitted.PersistedAdoptedContext);
        Assert.Equal(["user-123", "user-456"], emitted.AdoptedSpeakerIds);
    }
}
