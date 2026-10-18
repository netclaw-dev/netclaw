// -----------------------------------------------------------------------
// <copyright file="TurnCompletedQuietEndWireTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// <see cref="TurnCompleted.QuietEnd"/> tells a channel binding that the session ended the turn on purpose
/// with no reply, after the model ignored the escalated nudge for a refused repeat. The binding then posts
/// no empty-turn fallback, because Netclaw must not write text that the model did not write. The flag
/// crosses the hub in <see cref="SessionOutputDto.QuietEnd"/> (wire name <c>quietEnd</c>). An absent field
/// comes from an old daemon and reads as false, so the fallback stays on for every other empty turn.
/// </summary>
public sealed class TurnCompletedQuietEndWireTests
{
    private static readonly SessionId Session = new("test/wire-quiet");

    private static readonly JsonSerializerOptions Hub = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_flag_round_trips(bool quietEnd)
    {
        // Planted bug: the mapper drops the flag, or writes true for every turn.
        var original = Turn(quietEnd);

        var result = Assert.IsType<TurnCompleted>(
            SessionOutputDtoMapper.FromDto(SessionOutputDtoMapper.ToDto(original)));

        Assert.Equal(quietEnd, result.QuietEnd);
        Assert.Equal(TurnOutcome.Completed, result.Outcome);
    }

    [Fact]
    public void A_turn_defaults_to_no_quiet_end()
    {
        Assert.False(Turn().QuietEnd);
    }

    [Fact]
    public void The_wire_name_is_quietEnd()
    {
        var dto = SessionOutputDtoMapper.ToDto(Turn(quietEnd: true));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, Hub));

        Assert.True(json.RootElement.GetProperty("quietEnd").GetBoolean());
    }

    [Theory]
    [InlineData("""{"type":"turn_completed","sessionId":"test/wire-quiet","timestampMs":1,"turnNumber":3,"turnOutcome":"completed"}""")]
    [InlineData("""{"type":"turn_completed","sessionId":"test/wire-quiet","timestampMs":1,"turnNumber":3,"turnOutcome":"completed","quietEnd":null}""")]
    public void A_turn_from_an_old_daemon_has_no_field_and_reads_as_false(string wire)
    {
        // Planted bug: the mapper throws on a missing field, or reads it as true. A true value would hide the
        // fallback for every empty turn that an old daemon reports.
        var dto = JsonSerializer.Deserialize<SessionOutputDto>(wire, Hub)!;

        var result = Assert.IsType<TurnCompleted>(SessionOutputDtoMapper.FromDto(dto));

        Assert.False(result.QuietEnd);
        Assert.Equal(3, result.TurnNumber.Value);
    }

    [Fact]
    public void A_wire_turn_with_the_flag_reads_as_a_quiet_end()
    {
        const string wire = """{"type":"turn_completed","sessionId":"test/wire-quiet","timestampMs":1,"turnNumber":1,"turnOutcome":"completed","quietEnd":true}""";

        var result = Assert.IsType<TurnCompleted>(
            SessionOutputDtoMapper.FromDto(JsonSerializer.Deserialize<SessionOutputDto>(wire, Hub)!));

        Assert.True(result.QuietEnd);
    }

    private static TurnCompleted Turn(bool quietEnd = false) => new()
    {
        SessionId = Session,
        TurnNumber = new TurnNumber(2),
        Outcome = TurnOutcome.Completed,
        QuietEnd = quietEnd,
        OpenChildRunIds = []
    };
}
