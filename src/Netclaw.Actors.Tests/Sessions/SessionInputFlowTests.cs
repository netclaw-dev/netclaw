// -----------------------------------------------------------------------
// <copyright file="SessionInputFlowTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionInputFlowTests
{
    [Fact]
    public void Terminal_commit_selects_only_the_next_ordered_batch()
    {
        // Arrange
        var sessionId = new SessionId("input-flow/selected-batch");
        var flow = new SessionInputFlow();
        var state = SessionState.Empty;
        var commands = new[] { "First", "Second", "Third" }
            .Select(content => new SendUserMessage { SessionId = sessionId, Content = content })
            .ToArray();
        var admissions = new List<InputAdmitted>();
        foreach (var command in commands)
        {
            var decision = flow.PrepareAdmission(command, state, sessionId, 1, childConsentPaused: false)
                as AdmissionDecision.Persist
                ?? throw new InvalidOperationException("The test input was not admitted.");
            admissions.Add(decision.Event);
            state = state.Apply(decision.Event);
        }

        // Act
        var first = flow.AdmissionCommitted(admissions[0], commands[0], InputPlacement.Activate);
        flow.ActivateInput(first, state.PendingInputs, null);
        flow.AdmissionCommitted(admissions[1], commands[1], InputPlacement.BufferDuringModel);
        flow.AdmissionCommitted(admissions[2], commands[2], InputPlacement.BufferDuringModel);
        var firstSelected = flow.SelectedInputs.Ids;
        flow.TurnCommitted(new TurnRecorded { SessionId = sessionId, ConsumedInputIds = [admissions[0].InputId] });
        var continuation = flow.AdvanceAfterTurn(false, childConsentPaused: false);
        var nextSelected = flow.SelectedInputs.Ids;
        flow.TurnCommitted(new TurnRecorded
        {
            SessionId = sessionId,
            ConsumedInputIds = [admissions[1].InputId, admissions[2].InputId]
        });
        var finalSelected = flow.SelectedInputs.Ids;
        var ready = flow.AdvanceAfterTurn(false, childConsentPaused: false);

        // Assert
        Assert.Equal([admissions[0].InputId], firstSelected);
        Assert.Equal(InputProgressAction.FollowUp, continuation.Action);
        var batch = Assert.IsType<BufferedInputBatch>(continuation.Batch);
        Assert.True(batch.StartsNewTurn);
        Assert.Equal(["Second", "Third"], batch.Messages.Select(message => message.Content));
        Assert.Equal([admissions[1].InputId, admissions[2].InputId], nextSelected);
        Assert.Empty(finalSelected);
        Assert.Equal(InputProgressAction.Ready, ready.Action);
    }

    [Fact]
    public void Actor_restart_copies_replay_and_buffered_inputs_in_order()
    {
        // Arrange
        var sessionId = new SessionId("input-flow/restart-copy");
        var flow = new SessionInputFlow();
        var replay = new SendUserMessage
        {
            SessionId = sessionId, Content = "Replay", AdmittedInputId = new InputId("replay-id")
        };
        var first = new SendUserMessage
        {
            SessionId = sessionId, Content = "Buffered first", AdmittedInputId = new InputId("first-id")
        };
        var second = new SendUserMessage
        {
            SessionId = sessionId, Content = "Buffered second", AdmittedInputId = new InputId("second-id")
        };
        flow.AdmissionAlreadyCommitted(first, InputPlacement.BufferDuringModel);
        flow.AdmissionAlreadyCommitted(second, InputPlacement.BufferDuringCompaction);
        flow.BufferReplay(replay);

        // Act
        var copied = flow.PrepareActorRestartReplay();
        var copiedAgain = flow.PrepareActorRestartReplay();

        // Assert
        Assert.Equal([replay.AdmittedInputId, first.AdmittedInputId, second.AdmittedInputId],
            copied.Select(message => message.AdmittedInputId));
        Assert.Equal(["Replay", "Buffered first", "Buffered second"],
            copied.Select(message => message.Content));
        Assert.Empty(copiedAgain);
    }
}
