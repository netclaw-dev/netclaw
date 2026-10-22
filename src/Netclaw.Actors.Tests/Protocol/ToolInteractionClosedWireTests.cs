// -----------------------------------------------------------------------
// <copyright file="ToolInteractionClosedWireTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// The TUI and the headless client receive session outputs through the SignalR hub as <see cref="SessionOutputDto"/>.
/// <see cref="ToolInteractionClosed"/> must cross the hub with its call ID and its reason, or the TUI keeps a withdrawn
/// prompt and the headless client cannot print the close (PR 2 of the change
/// <c>child-approval-events-and-parent-pause</c>, task 5.1).
/// Planted bug: the mapper has no case for the closed output, so it crosses as an unknown type and the client reads an
/// error (today).
/// </summary>
public sealed class ToolInteractionClosedWireTests
{
    [Theory]
    [InlineData(WithdrawReason.CancelledByParent)]
    [InlineData(WithdrawReason.ChildEnded)]
    [InlineData(WithdrawReason.ChildLost)]
    [InlineData(WithdrawReason.DaemonRestarted)]
    [InlineData(WithdrawReason.PromptUnavailable)]
    public void The_closed_output_round_trips_with_its_call_and_reason(WithdrawReason reason)
    {
        var original = new ToolInteractionClosed(new ToolCallId("run-1/subagent-approval/2"), new ToolInteractionCloseReason.Withdrawn(reason))
        {
            SessionId = new SessionId("test/wire-closed")
        };

        var result = Assert.IsType<ToolInteractionClosed>(
            SessionOutputDtoMapper.FromDto(SessionOutputDtoMapper.ToDto(original)));

        Assert.Equal(original.CallId, result.CallId);
        Assert.Equal(new ToolInteractionCloseReason.Withdrawn(reason), result.Reason);
        Assert.Equal(original.SessionId, result.SessionId);
    }

    // Review nit of PR 2: the close of a prompt that a person answered on another surface crosses the hub as "answered".
    // Planted bug: the mapper writes or reads every close as a withdrawal.
    [Fact]
    public void An_answered_close_round_trips_with_its_call_and_reason()
    {
        var original = new ToolInteractionClosed(new ToolCallId("run-1/subagent-approval/2"), new ToolInteractionCloseReason.Answered())
        {
            SessionId = new SessionId("test/wire-closed")
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        var result = Assert.IsType<ToolInteractionClosed>(SessionOutputDtoMapper.FromDto(dto));

        Assert.Null(dto.InteractionWithdrawReason);
        Assert.Equal(original.CallId, result.CallId);
        Assert.IsType<ToolInteractionCloseReason.Answered>(result.Reason);
        Assert.Equal(original.SessionId, result.SessionId);
    }

    // Review nit of PR 2: a close with no reason, an unknown reason, a withdrawal with no withdraw reason, or an answer
    // that also carries a withdraw reason fails the read. A guess would show the operator a false reason, and a
    // contradictory frame is a sender defect that must not pass as one of its two readings.
    // Planted bug: the reader takes a missing or unknown kind as a withdrawal, or it ignores the withdraw reason of an
    // "answered" frame (today for the last case).
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "CancelledByParent")]
    [InlineData("expired", null)]
    [InlineData("withdrawn", null)]
    [InlineData("withdrawn", "NotAReason")]
    [InlineData("answered", "CancelledByParent")]
    public void A_close_with_a_missing_unknown_or_contradictory_reason_fails_the_read(string? kind, string? withdrawReason)
    {
        var valid = SessionOutputDtoMapper.ToDto(new ToolInteractionClosed(
            new ToolCallId("run-1/subagent-approval/2"), new ToolInteractionCloseReason.Answered())
        {
            SessionId = new SessionId("test/wire-closed")
        });
        var dto = valid with { InteractionCloseKind = kind, InteractionWithdrawReason = withdrawReason };

        Assert.Throws<InvalidDataException>(() => SessionOutputDtoMapper.FromDto(dto));
    }
}
