// -----------------------------------------------------------------------
// <copyright file="SessionToolWorkflowRequesterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class SessionToolWorkflowTests
{
    [Theory]
    [InlineData("other-user", ApprovalOptionKeys.ApproveOnce, ApprovalNackReasons.WrongRequester)]
    [InlineData("user-1", ApprovalOptionKeys.ApproveAlways, ApprovalNackReasons.OptionUnavailable)]
    public void Recovered_response_rejects_unauthorized_answers(
        string senderId,
        string optionKey,
        string expectedReason)
    {
        // Arrange
        var workflow = new SessionToolWorkflow();
        var request = CreateRequest("call-recovered", requestedAtMs: 10);
        workflow.ApplyApprovalRequested(request, durable: true, recovered: true);

        // Act
        var response = workflow.PrepareRecoveredResponse(
            CreateResponse(request, senderId, optionKey), SessionState.Empty);

        // Assert
        Assert.Equal(expectedReason, Assert.IsType<ApprovalResponseResult.Rejected>(response).NackReason);
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

        return new ToolApprovalRequested
        {
            SessionId = context.SessionId,
            CallId = callId,
            AuthorizationAttemptId = $"auth-{Guid.NewGuid():N}",
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

    private static ToolInteractionResponse CreateResponse(
        ToolApprovalRequested request,
        string senderId,
        string optionKey)
        => new()
        {
            SessionId = request.SessionId,
            CallId = new ToolCallId(request.CallId),
            SelectedKey = new ApprovalOptionKey(optionKey),
            SenderId = new SenderId(senderId)
        };
}
