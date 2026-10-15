// -----------------------------------------------------------------------
// <copyright file="ReminderFireDropRuleTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Deterministic cases for the drop decision of <see cref="SessionInputFlow.PrepareAdmission"/>. Each case fixes one
/// fact of the rule, so these cases kill the focused mutants of the decision
/// (<c>scripts/run-reminder-execution-mutations.sh</c>). <c>ReminderFireAdmissionAccordantTests</c> states the rule
/// over generated sequences, and <c>ReminderFireAdmissionTests</c> proves it on the session actor.
/// </summary>
public sealed class ReminderFireDropRuleTests
{
    [Fact]
    public void A_newer_fire_is_dropped_while_an_earlier_fire_of_the_reminder_waits()
    {
        // Arrange: an operator turn runs, and the 10:00 fire waits behind it.
        var session = new Session();
        session.Activate("alice");
        session.Admit(Fire("build:1000"), InputPlacement.BufferDuringModel);

        // Act
        var decision = session.Prepare(Fire("build:2000"));

        // Assert
        var dropped = Assert.IsType<AdmissionDecision.Dropped>(decision);
        Assert.Equal(new ReminderFireKey(new ReminderId("build"), 2000), dropped.Fire);
        Assert.Equal(new ReminderFireKey(new ReminderId("build"), 1000), dropped.Waiting);
    }

    [Fact]
    public void A_newer_fire_is_admitted_while_the_earlier_fire_runs()
    {
        // Arrange: the 10:00 fire started the running turn, so the turn selected it.
        var session = new Session();
        session.Activate(Fire("build:1000"));

        // Act
        var decision = session.Prepare(Fire("build:2000"));

        // Assert
        Assert.IsType<AdmissionDecision.Persist>(decision);
    }

    [Fact]
    public void The_next_fire_is_admitted_once_the_earlier_fire_is_consumed()
    {
        // Arrange
        var session = new Session();
        session.Activate("alice");
        session.Admit(Fire("build:1000"), InputPlacement.BufferDuringModel);
        session.FinishTurnAndStartNext();

        // Act
        var decision = session.Prepare(Fire("build:2000"));

        // Assert: the earlier fire now runs, and a running fire does not block.
        Assert.IsType<AdmissionDecision.Persist>(decision);
    }

    [Theory]
    [InlineData("check", "check-pr")]
    [InlineData("check-pr", "check")]
    [InlineData("standup", "build")]
    public void A_fire_of_another_reminder_is_never_dropped(string waiting, string incoming)
    {
        // Arrange
        var session = new Session();
        session.Activate("alice");
        session.Admit(Fire($"{waiting}:1000"), InputPlacement.BufferDuringModel);

        // Act
        var decision = session.Prepare(Fire($"{incoming}:2000"));

        // Assert
        Assert.IsType<AdmissionDecision.Persist>(decision);
    }

    [Fact]
    public void An_operator_input_that_looks_like_a_fire_key_neither_blocks_nor_drops()
    {
        // Arrange: the operator message id has the shape of a fire key. Only a reminder input owns a fire key.
        var session = new Session();
        session.Activate("alice");
        session.Admit(Operator("bob", messageId: "build:1000"), InputPlacement.BufferDuringModel);

        // Act
        var fireDecision = session.Prepare(Fire("build:2000"));
        var operatorDecision = session.Prepare(Operator("carol", messageId: "build:3000"));

        // Assert
        Assert.IsType<AdmissionDecision.Persist>(fireDecision);
        Assert.IsType<AdmissionDecision.Persist>(operatorDecision);
    }

    [Theory]
    [InlineData("weekly:abc", "weekly:def")]
    [InlineData("weekly:", "weekly:1")]
    [InlineData("alpha", "beta")]
    [InlineData("a:b:1", "a:b:2")]
    public void A_key_that_is_not_a_fire_key_never_blocks_and_is_never_dropped(string waiting, string incoming)
    {
        // Arrange
        var session = new Session();
        session.Activate("alice");
        session.Admit(Fire(waiting), InputPlacement.BufferDuringModel);

        // Act
        var decision = session.Prepare(Fire(incoming));

        // Assert
        Assert.IsType<AdmissionDecision.Persist>(decision);
    }

    [Fact]
    public void A_redelivered_fire_is_a_duplicate_and_not_a_drop()
    {
        // Arrange
        var session = new Session();
        session.Activate("alice");
        session.Admit(Fire("build:1000"), InputPlacement.BufferDuringModel);

        // Act
        var decision = session.Prepare(Fire("build:1000"));

        // Assert
        Assert.IsType<AdmissionDecision.Duplicate>(decision);
    }

    private static SendUserMessage Fire(string key) => new()
    {
        SessionId = Session.Id,
        Content = $"Fire {key}",
        Source = new MessageSource
        {
            ChannelType = ChannelType.Slack,
            SenderId = new SenderId("reminder-system"),
            ChannelId = "channel",
            MessageId = key,
            TurnId = new TurnId(key),
            ReminderId = new ReminderId(key),
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
            {
                SourceKind = new SourceKind("reminder")
            }
        }
    };

    private static SendUserMessage Operator(string requester, string? messageId = null) => new()
    {
        SessionId = Session.Id,
        Content = $"Message from {requester}",
        Source = new MessageSource
        {
            ChannelType = ChannelType.Slack,
            SenderId = new SenderId(requester),
            ChannelId = "channel",
            MessageId = messageId,
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        }
    };

    // Makes the same calls as LlmSessionActor: admission, placement, activation, and turn commit.
    private sealed class Session
    {
        public static readonly SessionId Id = new("drop-rule/session");
        private readonly SessionInputFlow _flow = new();
        private SessionState _state = SessionState.Empty;

        public AdmissionDecision Prepare(SendUserMessage command) => _flow.PrepareAdmission(command, _state, Id, 1);

        public void Activate(string requester) => Activate(Operator(requester));

        public void Activate(SendUserMessage command)
        {
            var accepted = Admit(command, InputPlacement.Activate);
            _flow.ActivateInput(accepted, _state.PendingInputs, null);
        }

        public SendUserMessage Admit(SendUserMessage command, InputPlacement placement)
        {
            var persist = Assert.IsType<AdmissionDecision.Persist>(Prepare(command));
            _state = _state.Apply(persist.Event);
            return _flow.AdmissionCommitted(persist.Event, command, placement);
        }

        // The running turn records and the buffered input starts the next turn. A buffered input of the
        // same requester would join, so the operator turn and the fire have different requesters.
        public void FinishTurnAndStartNext()
        {
            var recorded = new TurnRecorded { SessionId = Id, ConsumedInputIds = _flow.SelectedInputs.Ids };
            _state = _state.Apply(recorded, userAlreadyInHistory: true);
            _flow.TurnCommitted(recorded);
            var next = _flow.AdvanceAfterTurn(restartDrainRequested: false);
            Assert.Equal(InputProgressAction.FollowUp, next.Action);
        }
    }
}
