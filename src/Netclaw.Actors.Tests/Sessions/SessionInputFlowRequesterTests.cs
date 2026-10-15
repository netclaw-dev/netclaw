// -----------------------------------------------------------------------
// <copyright file="SessionInputFlowRequesterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// One turn has one requester. These deterministic cases kill the focused mutants of the same-requester rule in
/// <see cref="SessionInputFlow"/> (<c>scripts/run-input-one-authority-mutations.sh</c>).
/// <c>SessionInputFlowAccordantTests</c> states the same rule over generated sequences.
/// </summary>
public sealed class SessionInputFlowRequesterTests
{
    private const string Alice = "alice";
    private const string Bob = "bob";

    [Fact]
    public void Each_follow_up_turn_serves_one_requester_in_admission_order()
    {
        // Arrange
        var flow = new Flow();
        flow.Activate(Alice, "a0");
        flow.Buffer(Bob, "b1");
        flow.Buffer(Bob, "b2");
        flow.Buffer(Alice, "a3");

        // Act
        var bobTurn = flow.FinishTurn();
        var bobRequester = flow.Requester;
        var aliceTurn = flow.FinishTurn();
        var aliceRequester = flow.Requester;
        var ready = flow.FinishTurn();

        // Assert
        Assert.Equal(["b1", "b2"], Contents(bobTurn));
        Assert.True(bobTurn.Batch!.AdoptsAuthority);
        Assert.Equal(Bob, bobRequester);
        Assert.Equal(["a3"], Contents(aliceTurn));
        Assert.True(aliceTurn.Batch!.AdoptsAuthority);
        Assert.Equal(Alice, aliceRequester);
        Assert.Equal(InputProgressAction.Ready, ready.Action);
    }

    [Fact]
    public void A_running_turn_admits_only_input_of_its_own_requester()
    {
        // Arrange
        var flow = new Flow();
        flow.Activate(Alice, "a0");
        flow.Buffer(Alice, "a1");
        flow.Buffer(Bob, "b1");
        flow.Buffer(Alice, "a2");

        // Act
        var joined = flow.ToolBoundary();
        var joinedRequester = flow.Requester;
        var blocked = flow.ToolBoundary();
        var blockedRequester = flow.Requester;
        var bobTurn = flow.FinishTurn();

        // Assert: Bob's input heads the buffer, so nothing more joins Alice's turn. Order is kept.
        Assert.Equal(["a1"], joined.Batch!.Messages.Select(static message => message.Content));
        Assert.False(joined.Batch.AdoptsAuthority);
        Assert.Equal(Alice, joinedRequester);
        Assert.Null(blocked.Batch);
        Assert.Equal(Alice, blockedRequester);
        Assert.Equal(["b1"], Contents(bobTurn));
        Assert.Equal(Bob, flow.Requester);
    }

    [Fact]
    public void A_replay_continues_the_current_turn_and_input_of_another_requester_waits()
    {
        // Arrange
        var flow = new Flow();
        flow.Activate(Alice, "a0");
        flow.Buffer(Bob, "b1");
        flow.Replay("a0 again");

        // Act
        var replay = flow.FinishCompaction();
        var replayRequester = flow.Requester;
        var bobTurn = flow.FinishTurn();

        // Assert
        Assert.Equal(["a0 again"], replay.Batch!.Messages.Select(static message => message.Content));
        Assert.False(replay.Batch.AdoptsAuthority);
        Assert.Equal(Alice, replayRequester);
        Assert.Equal(["b1"], Contents(bobTurn));
        Assert.Equal(Bob, flow.Requester);
    }

    private static IEnumerable<string> Contents(AfterTurnResult result)
    {
        Assert.Equal(InputProgressAction.FollowUp, result.Action);
        return result.Batch!.Messages.Select(static message => message.Content);
    }

    // Makes the same calls as LlmSessionActor: journal admission, placement, turn commit, and drain.
    private sealed class Flow
    {
        private static readonly SessionId SessionId = new("input-flow/one-requester");
        private readonly SessionInputFlow _flow = new();
        private SessionState _state = SessionState.Empty;

        public string? Requester => _flow.Authority.Context?.RequesterSenderId?.Value;

        public void Activate(string requester, string content)
        {
            var accepted = Admit(requester, content, InputPlacement.Activate);
            _flow.ActivateInput(accepted, _state.PendingInputs, null);
        }

        public void Buffer(string requester, string content)
            => Admit(requester, content, InputPlacement.BufferDuringModel);

        public void Replay(string content)
            => _flow.BufferReplay(new SendUserMessage { SessionId = SessionId, Content = content });

        public AfterTurnResult FinishTurn()
        {
            var recorded = new TurnRecorded { SessionId = SessionId, ConsumedInputIds = _flow.SelectedInputs.Ids };
            _state = _state.Apply(recorded, userAlreadyInHistory: true);
            _flow.TurnCommitted(recorded);
            return _flow.AdvanceAfterTurn(restartDrainRequested: false);
        }

        public ToolBatchInputAdvance ToolBoundary()
        {
            _state = _state.CloseInputs(_flow.SelectedInputs.Ids);
            _flow.ToolBatchCommitted();
            return _flow.AdvanceAfterToolBatch(
                new BatchCompletion.Ready(_state, 1, ToolBudgetStatus.Ok.Instance), compactionDue: false);
        }

        public CompactionContinuation FinishCompaction() => _flow.AdvanceAfterCompaction(restartDrainRequested: false);

        private SendUserMessage Admit(string requester, string content, InputPlacement placement)
        {
            var command = new SendUserMessage { SessionId = SessionId, Content = content, Source = Source(requester) };
            var persist = Assert.IsType<AdmissionDecision.Persist>(_flow.PrepareAdmission(command, _state, SessionId, 1));
            _state = _state.Apply(persist.Event);
            return _flow.AdmissionCommitted(persist.Event, command, placement);
        }

        private static MessageSource Source(string requester) => new()
        {
            ChannelType = ChannelType.SignalR,
            SenderId = new SenderId(requester),
            ChannelId = "operator-channel",
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        };
    }
}
