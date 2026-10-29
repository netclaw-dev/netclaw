// -----------------------------------------------------------------------
// <copyright file="ToolInteractionNotesWireTests.cs" company="Petabridge, LLC">
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
/// The approval notes travel beside the command, not inside it. The TUI reads them from the hub DTO. The field is
/// optional: a daemon that sends none reads as no notes.
/// </summary>
public sealed class ToolInteractionNotesWireTests
{
    private static readonly JsonSerializerOptions Hub = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Notes_cross_the_hub_and_leave_the_command_unchanged()
    {
        var original = Request() with { Notes = "Reusable grants cover the command words shown above within the selected scope." };

        var dto = SessionOutputDtoMapper.ToDto(original);
        var wire = JsonSerializer.Deserialize<SessionOutputDto>(JsonSerializer.Serialize(dto, Hub), Hub)!;
        var result = Assert.IsType<ToolInteractionRequest>(SessionOutputDtoMapper.FromDto(wire));

        Assert.Equal(original.Notes, result.Notes);
        Assert.Equal("git push origin main", result.DisplayText);
    }

    [Fact]
    public void A_payload_without_notes_reads_as_no_notes()
    {
        var dto = SessionOutputDtoMapper.ToDto(Request());
        var wire = JsonSerializer.Deserialize<SessionOutputDto>(JsonSerializer.Serialize(dto, Hub), Hub)!;

        var result = Assert.IsType<ToolInteractionRequest>(SessionOutputDtoMapper.FromDto(wire));

        Assert.Null(result.Notes);
    }

    private static ToolInteractionRequest Request()
        => new()
        {
            SessionId = new SessionId("test/session"),
            Kind = "approval",
            CallId = new ToolCallId("call-notes"),
            ToolName = new ToolName("shell_execute"),
            DisplayText = "git push origin main",
            Options = [new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel)]
        };
}
