// -----------------------------------------------------------------------
// <copyright file="NewInputAbandonmentPlanTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Executable spec: who may abandon an open approval. This is an authority boundary.
/// <list type="bullet">
/// <item>Automated input (a reminder fire, a webhook, a background job result) waits. The approval stays open.</item>
/// <item>Input from a person closes the approval, then runs. A message with no known principal counts as a person.</item>
/// <item>An approval that already has an answer is not open. Any input closes its batch first.</item>
/// </list>
/// </summary>
public sealed class NewInputAbandonmentPlanTests
{
    private static readonly SessionId Session = new("session-1");

    [Theory]
    [InlineData(PrincipalClassification.VerifiedAutomation)]
    [InlineData(PrincipalClassification.SystemProcess)]
    public void Automated_input_waits_and_leaves_the_approval_open(PrincipalClassification principal)
    {
        // Arrange
        var workflow = WorkflowWithOpenApproval();

        // Act
        var plan = workflow.PlanNewInputAbandonment(SessionState.Empty, Session, principal, nowMs: 100);

        // Assert
        Assert.IsType<NewInputPlan.WaitForApproval>(plan);
        Assert.True(workflow.HasPendingApprovals());
    }

    [Theory]
    [InlineData(PrincipalClassification.Operator)]
    [InlineData(PrincipalClassification.TrustedInternal)]
    [InlineData(PrincipalClassification.UntrustedExternal)]
    [InlineData(null)]
    public void Input_from_a_person_closes_the_open_approval_before_its_own_turn(PrincipalClassification? principal)
    {
        // Arrange
        var workflow = WorkflowWithOpenApproval();

        // Act
        var plan = workflow.PlanNewInputAbandonment(SessionState.Empty, Session, principal, nowMs: 100);

        // Assert
        Assert.IsType<NewInputPlan.AbandonThenActivate>(plan);
    }

    [Theory]
    [InlineData(PrincipalClassification.VerifiedAutomation)]
    [InlineData(PrincipalClassification.Operator)]
    public void An_answered_approval_is_not_open_so_any_input_closes_its_batch(PrincipalClassification principal)
    {
        // Arrange
        var workflow = WorkflowWithOpenApproval();
        var request = Request("call-answered");
        var accepted = Assert.IsAssignableFrom<ApprovalResponseResult.DurableAccepted>(
            workflow.PrepareRecoveredResponse(
                new ToolInteractionResponse
                {
                    SessionId = Session,
                    CallId = new Netclaw.Tools.ToolCallId(request.CallId),
                    SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
                    SenderId = new SenderId("user-1")
                },
                SessionState.Empty));
        workflow.ApplyResolvedResponse(workflow.CreateResolution(accepted, Session, nowMs: 50));

        // Act
        var plan = workflow.PlanNewInputAbandonment(SessionState.Empty, Session, principal, nowMs: 100);

        // Assert
        Assert.IsType<NewInputPlan.AbandonThenActivate>(plan);
    }

    private static SessionToolWorkflow WorkflowWithOpenApproval()
    {
        var workflow = new SessionToolWorkflow();
        workflow.ApplyApprovalRequested(Request("call-answered"), durable: true, recovered: true);
        return workflow;
    }

    private static ToolApprovalRequested Request(string callId)
    {
        var context = new TurnContext
        {
            SessionId = Session,
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
            SessionId = Session,
            CallId = callId,
            AuthorizationAttemptId = "auth-1",
            ToolName = "shell_execute",
            Patterns = ["git status"],
            CandidateVerbs = ["git"],
            Audience = context.Audience,
            Boundary = context.Boundary,
            ChannelType = context.ChannelType?.ToWireValue(),
            SupportsInteractiveApproval = true,
            RequesterSenderId = context.RequesterSenderId,
            RequesterPrincipal = context.RequesterPrincipal,
            Cwd = "/repo",
            OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            TurnContext = context.ToRecord(),
            RequestedAtMs = 10
        };
    }
}
