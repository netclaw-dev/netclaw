// -----------------------------------------------------------------------
// <copyright file="TurnCompletedOpenChildRunsWireTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// The open child runs of a turn cross the hub in <see cref="SessionOutputDto.OpenChildRunIds"/> (wire name
/// <c>openChildRunIds</c>). A new daemon writes the field for every <see cref="TurnCompleted"/>. A missing
/// field comes from an old daemon and reads as an empty list. The contract is in the
/// <c>background-subagent-runs</c> and <c>netclaw-session</c> specs.
/// </summary>
public sealed class TurnCompletedOpenChildRunsWireTests
{
    private static readonly SessionId Session = new("test/wire");

    private static readonly JsonSerializerOptions Hub = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    [InlineData(TurnOutcome.Skipped)]
    public void Open_runs_round_trip_in_order_for_every_outcome(TurnOutcome outcome)
    {
        // Planted bug: the mapper drops the field, or sorts or deduplicates the runs.
        var original = Turn(outcome, "run-b", "run-a");

        var result = Assert.IsType<TurnCompleted>(
            SessionOutputDtoMapper.FromDto(SessionOutputDtoMapper.ToDto(original)));

        Assert.Equal(["run-b", "run-a"], result.OpenChildRunIds.Select(id => id.Value).ToArray());
        Assert.Equal(outcome, result.Outcome);
    }

    [Fact]
    public void A_turn_with_no_open_run_writes_an_empty_array_not_an_absent_field()
    {
        // A new daemon always writes the field. An absent field means "earlier daemon", so an empty
        // list must not look like one. Planted bug: the mapper writes null for an empty list.
        var dto = SessionOutputDtoMapper.ToDto(Turn(TurnOutcome.Completed));

        Assert.NotNull(dto.OpenChildRunIds);
        Assert.Empty(dto.OpenChildRunIds);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, Hub));
        var field = json.RootElement.GetProperty("openChildRunIds");
        Assert.Equal(JsonValueKind.Array, field.ValueKind);
        Assert.Equal(0, field.GetArrayLength());
    }

    [Fact]
    public void The_wire_name_is_openChildRunIds()
    {
        var dto = SessionOutputDtoMapper.ToDto(Turn(TurnOutcome.Completed, "run-1"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, Hub));

        Assert.Equal(
            ["run-1"],
            json.RootElement.GetProperty("openChildRunIds").EnumerateArray().Select(id => id.GetString()).ToArray());
    }

    [Theory]
    [InlineData("""{"type":"turn_completed","sessionId":"test/wire","timestampMs":1,"turnNumber":3,"turnOutcome":"completed"}""")]
    [InlineData("""{"type":"turn_completed","sessionId":"test/wire","timestampMs":1,"turnNumber":3,"turnOutcome":"completed","openChildRunIds":null}""")]
    public void A_turn_from_an_old_daemon_has_no_field_and_reads_as_an_empty_list(string wire)
    {
        // Planted bug: the mapper throws on a missing field, or reads it as an open run.
        var dto = JsonSerializer.Deserialize<SessionOutputDto>(wire, Hub)!;

        var result = Assert.IsType<TurnCompleted>(SessionOutputDtoMapper.FromDto(dto));

        Assert.Empty(result.OpenChildRunIds);
        Assert.Equal(3, result.TurnNumber.Value);
        Assert.Equal(TurnOutcome.Completed, result.Outcome);
    }

    [Fact]
    public void A_wire_turn_with_open_runs_reads_as_a_list_of_run_ids()
    {
        const string wire = """{"type":"turn_completed","sessionId":"test/wire","timestampMs":1,"turnNumber":1,"turnOutcome":"completed","openChildRunIds":["run-1","run-2"]}""";

        var result = Assert.IsType<TurnCompleted>(
            SessionOutputDtoMapper.FromDto(JsonSerializer.Deserialize<SessionOutputDto>(wire, Hub)!));

        Assert.Equal(["run-1", "run-2"], result.OpenChildRunIds.Select(id => id.Value).ToArray());
    }

    [Fact]
    public void Session_deactivation_survives_the_wire()
    {
        // Planted bug: the mapper has no case for SessionDeactivated. The client then reads the unknown type
        // as an error output, and a waiting headless run never learns that the session stopped.
        var dto = SessionOutputDtoMapper.ToDto(new SessionDeactivated { SessionId = Session });

        Assert.Equal("session_deactivated", dto.Type);
        var result = SessionOutputDtoMapper.FromDto(dto);
        Assert.IsType<SessionDeactivated>(result);
        Assert.Equal(Session, result.SessionId);
    }

    private static TurnCompleted Turn(TurnOutcome outcome, params string[] open) => new()
    {
        SessionId = Session,
        TurnNumber = new TurnNumber(2),
        Outcome = outcome,
        OpenChildRunIds = [.. open.Select(id => new SubAgentRunId(id))]
    };
}
