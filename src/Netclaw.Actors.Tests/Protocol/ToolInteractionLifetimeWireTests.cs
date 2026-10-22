// -----------------------------------------------------------------------
// <copyright file="ToolInteractionLifetimeWireTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Google.Protobuf;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// Task 1.10 (wire part). A child prompt lives until it closes, so its lifetime must reach every consumer: the TUI
/// reads it from the hub DTO, and a channel binding reads it from its journal after a cold spawn. The binding journal
/// field is optional. A row that an older binary wrote has no field and reads as a turn prompt, which is today's
/// behavior. Source: design D3 "Lifetime (PR 1)" and the format table "PR 1".
/// </summary>
public sealed class ToolInteractionLifetimeWireTests : TestKit
{
    private static readonly JsonSerializerOptions Hub = new(JsonSerializerDefaults.Web);

    public ToolInteractionLifetimeWireTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.WithNetclawSerialization();

    /// <summary>
    /// Planted bug: the hub mapper drops the lifetime, so the TUI clears a child prompt at the end of a parent turn.
    /// </summary>
    [Fact]
    public void A_child_prompt_lifetime_crosses_the_hub()
    {
        // Arrange
        const ToolInteractionLifetime lifetime = ToolInteractionLifetime.UntilClosed;
        var original = Request() with { Lifetime = lifetime };

        // Act
        var dto = SessionOutputDtoMapper.ToDto(original);
        var wire = JsonSerializer.Deserialize<SessionOutputDto>(JsonSerializer.Serialize(dto, Hub), Hub)!;
        var result = Assert.IsType<ToolInteractionRequest>(SessionOutputDtoMapper.FromDto(wire));

        // Assert
        Assert.Equal(lifetime, result.Lifetime);
    }

    /// <summary>
    /// Planted bug: the binding journal drops the lifetime, so a cold-spawned binding clears a child prompt at the
    /// next turn end.
    /// </summary>
    [Fact]
    public void A_tracked_child_prompt_keeps_its_lifetime_in_the_binding_journal()
    {
        // Arrange
        var serializer = Codec();
        var original = Tracked() with { Lifetime = ToolInteractionLifetime.UntilClosed };

        // Act
        var result = Assert.IsType<PendingApprovalPromptTracked>(
            serializer.FromBinary(serializer.ToBinary(original), serializer.Manifest(original)));

        // Assert
        Assert.Equal(ToolInteractionLifetime.UntilClosed, result.Lifetime);
        Assert.Equal(original.CallId, result.CallId);
        Assert.Equal(original.PromptId, result.PromptId);
    }

    /// <summary>
    /// The field is optional. The bytes of a row from an older binary have no lifetime field, and the row reads as a
    /// turn prompt. A turn prompt writes no field, so an older binary reads the new row the same way.
    /// Planted bug: a missing field reads as a child prompt, or a turn prompt writes the field.
    /// </summary>
    [Fact]
    public void A_row_without_the_field_reads_as_a_turn_prompt()
    {
        // Arrange: the bytes of a row that a binary before this change wrote.
        var serializer = Codec();
        var oldRow = new Netclaw.Actors.Serialization.Proto.PendingApprovalPromptTrackedProto
        {
            CallId = "r-7f2/subagent-approval/1",
            PromptId = "1779898078.594569",
            ToolName = "shell_execute",
            DisplayText = "gh pr view 12"
        };
        oldRow.OptionKeys.Add("approve_once");
        var manifest = serializer.Manifest(Tracked());

        // Act
        var result = Assert.IsType<PendingApprovalPromptTracked>(serializer.FromBinary(oldRow.ToByteArray(), manifest));
        var turnPromptBytes = serializer.ToBinary(Tracked() with { Lifetime = ToolInteractionLifetime.UntilTurnEnd });
        var childPromptBytes = serializer.ToBinary(Tracked() with { Lifetime = ToolInteractionLifetime.UntilClosed });

        // Assert
        Assert.Equal(ToolInteractionLifetime.UntilTurnEnd, result.Lifetime);
        Assert.Equal(serializer.ToBinary(Tracked()), turnPromptBytes);
        Assert.NotEqual(turnPromptBytes, childPromptBytes);
    }

    private NetclawProtobufSerializer Codec() => new((Akka.Actor.ExtendedActorSystem)Sys);

    private static PendingApprovalPromptTracked Tracked() => new()
    {
        CallId = "r-7f2/subagent-approval/1",
        RequesterSenderId = "U-alice",
        OptionKeys = ["approve_once", "deny"],
        PromptId = "1779898078.594569",
        ToolName = "shell_execute",
        DisplayText = "gh pr view 12"
    };

    private static ToolInteractionRequest Request() => new()
    {
        SessionId = new SessionId("tui/wire-lifetime"),
        TimestampMs = 1,
        Kind = "approval",
        CallId = new ToolCallId("r-7f2/subagent-approval/1"),
        ToolName = new ToolName("shell_execute"),
        DisplayText = "gh pr view 12",
        Options =
        [
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        ]
    };
}
