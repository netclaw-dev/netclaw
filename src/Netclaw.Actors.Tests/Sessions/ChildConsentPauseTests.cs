// -----------------------------------------------------------------------
// <copyright file="ChildConsentPauseTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// durable-child-consent-wait, task 2.4 (design D5, "One pause predicate"). Two code paths decide which input the
/// child consent pause holds: the drain of the input buffer and the recovery after a restart. They must hold the
/// same inputs, because the recovery queues exactly the inputs that the drain held. PR 0 puts the rule in one
/// predicate, <see cref="ChildConsentPause.Holds"/>, and both paths call it. PR 2 adds the restart-resume path to
/// this test.
/// </summary>
public sealed class ChildConsentPauseTests
{
    private static readonly SessionId Session = new("test/child-consent-pause-predicate");

    /// <summary>
    /// One set of inputs goes through the drain path, the recovery path, and the shared predicate. The pause holds
    /// automated input (<c>VerifiedAutomation</c> and <c>SystemProcess</c>), a child result notice, and a webhook that
    /// claims the notice kind. The pause never holds a person, an <c>UntrustedExternal</c> sender, or an approval
    /// notice.
    /// Planted bugs: a path holds a person or an untrusted sender, lets automation or the webhook pass, holds the
    /// approval notice, or the two paths use different rules.
    /// </summary>
    [Fact]
    public void The_drain_and_the_recovery_path_hold_the_same_inputs()
    {
        // Arrange
        InputAdmitted[] inputs =
        [
            Input("person", Context("alice")),
            Input("untrusted", Context("mallory") with
            {
                Audience = TrustAudience.Public,
                RequesterPrincipal = PrincipalClassification.UntrustedExternal,
                Provenance = new SourceProvenance(TransportAuthenticity.Unverified, PayloadTaint.Public)
            }),
            Input("verified-automation", Context("reminder-system") with
            {
                RequesterPrincipal = PrincipalClassification.VerifiedAutomation,
                Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
                {
                    SourceKind = new SourceKind(ReminderExecutionActor.ReminderSourceKind)
                }
            }),
            Input("system-process", Context("system") with
            {
                RequesterPrincipal = PrincipalClassification.SystemProcess,
                Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
            }),
            // A child result notice carries the recorded requester of the person who owns the run.
            Input("child-result", Context("alice")) with
            {
                ChildNotice = new ChildNotice(new SubAgentRunId("run-child-result"), null)
            },
            SessionInputFlow.PrepareConsentNotice(Session, Context("alice").ToRecord(), "approval-notice", 1),
            // A webhook can name its event type and its sender child-consent. The webhook path sets the authenticity
            // Verified, so the input is not a notice.
            Input("webhook-poser", NoticeContext() with
            {
                Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
                {
                    SourceKind = new SourceKind(ChildConsentNotice.Kind)
                }
            })
        ];
        string[] expected = ["child-result", "system-process", "verified-automation", "webhook-poser"];

        // Act
        var drain = HeldByTheDrain(inputs);
        var recovery = HeldByTheRecovery(inputs);
        var predicate = HeldByThePredicate(inputs);

        // Assert
        Assert.Equal(expected, drain);
        Assert.Equal(expected, recovery);
        Assert.Equal(expected, predicate);
    }

    // The drain (SessionInputFlow.IsHeldByPause): while the pause applies, each turn starts at the first input that
    // the pause does not hold. The inputs that never start a turn are the held set.
    private static string[] HeldByTheDrain(IReadOnlyList<InputAdmitted> inputs)
    {
        var flow = new SessionInputFlow();
        Assert.Equal(inputs.Count, flow.RestoreWaitingInputs(inputs, Session));
        var ran = new List<string>();
        for (var turn = 0; turn <= inputs.Count; turn++)
        {
            var result = flow.AdvanceAfterTurn(restartDrainRequested: false, childConsentPaused: true);
            if (result.Action == InputProgressAction.Ready)
                return Contents(inputs).Except(ran, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

            Assert.Equal(InputProgressAction.FollowUp, result.Action);
            ran.AddRange(result.Batch!.Messages.Select(static message => message.Content!));
        }

        Assert.Fail("The drain did not stop while the pause applied.");
        return [];
    }

    // The recovery (LlmSessionActor.StartInputsHeldByThePause): split off damaged records, then queue the valid inputs
    // that WasHeldByPause selects.
    private static string[] HeldByTheRecovery(IReadOnlyList<InputAdmitted> inputs)
    {
        var (valid, damaged) = SessionInputFlow.SplitByAuthority(inputs);
        Assert.Empty(damaged);
        return Contents(valid.Where(LlmSessionActor.WasHeldByPause).ToArray());
    }

    // The shared predicate gets the parsed authority, as each caller gives it after the split.
    private static string[] HeldByThePredicate(IReadOnlyList<InputAdmitted> inputs)
        => Contents(inputs.Where(static input =>
        {
            Assert.True(TurnContext.TryFromRecord(input.TurnContext, out var authority, out var reason), reason);
            return ChildConsentPause.Holds(authority!, isChildNotice: input.ChildNotice is not null);
        }).ToArray());

    private static string[] Contents(IReadOnlyList<InputAdmitted> inputs)
        => inputs.Select(static input => input.UserMessage.Content!).Order(StringComparer.Ordinal).ToArray();

    private static InputAdmitted Input(string content, TurnContext context) => new()
    {
        SessionId = Session,
        InputId = InputId.New(),
        UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = content },
        TurnContext = context.ToRecord() with { TurnId = $"turn-{content}" },
        AdmittedAtMs = 1
    };

    private static TurnContext NoticeContext()
    {
        var notice = SessionInputFlow.PrepareConsentNotice(Session, Context("alice").ToRecord(), "context", 1);
        Assert.True(TurnContext.TryFromRecord(notice.TurnContext, out var context, out var reason), reason);
        return context!;
    }

    private static TurnContext Context(string sender) => new()
    {
        SessionId = Session,
        TurnId = new TurnId($"turn-{sender}"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = ChannelType.Slack,
        RequesterSenderId = new SenderId(sender),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        SupportsInteractiveApproval = true
    };
}
