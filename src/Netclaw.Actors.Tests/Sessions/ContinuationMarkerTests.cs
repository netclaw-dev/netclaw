// -----------------------------------------------------------------------
// <copyright file="ContinuationMarkerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Google.Protobuf;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using InputAdmittedProto = Netclaw.Actors.Serialization.Proto.InputAdmittedProto;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// The explicit marker of the continuation input of a stopped turn (#2659, continuation-restart-followups design D4).
/// The session finds a continuation by its marker, never by its empty content, so a person input with no text and no
/// media stays an ordinary input. The journal and the snapshot keep the marker. A record that 0.28.0-beta.3 wrote has no
/// marker, and the reader keeps the old shape rule for it.
/// </summary>
public sealed class ContinuationMarkerTests
{
    private static readonly SessionId Owner = new("test/continuation-marker");

    /// <summary>
    /// Rows: empty-person (no text, no media, no marker: an ordinary input), marked-continuation (the drain wrote it),
    /// media-person (no text, one image: an ordinary input).
    /// Planted bug: the classifier reads the empty content in place of the marker.
    /// </summary>
    [Theory]
    [InlineData("empty-person", false)]
    [InlineData("marked-continuation", true)]
    [InlineData("media-person", false)]
    public void The_continuation_is_found_by_its_marker_not_by_its_empty_content(string row, bool expected)
    {
        // Arrange
        var input = row switch
        {
            "marked-continuation" => Input("") with { ContinuesStoppedTurn = true },
            "media-person" => Input("") with
            {
                UserMessage = new SerializableChatMessage
                {
                    Role = ChatRole.User,
                    MediaReferences =
                    [
                        new SerializableMediaReference
                        {
                            RelativePath = "photo.png", MimeType = new Netclaw.Media.MimeType("image/png"),
                            Modality = (int)MediaModality.Image
                        }
                    ]
                }
            },
            _ => Input("")
        };

        // Act
        var continuation = SessionInputFlow.IsContinuation(input);

        // Assert
        Assert.Equal(expected, continuation);
    }

    /// <summary>
    /// Rows:
    /// <list type="bullet">
    /// <item>journal-marked: a marked continuation keeps its marker through the journal bytes.</item>
    /// <item>journal-empty-person: an empty person input written by this binary stays unmarked.</item>
    /// <item>legacy-empty: a beta.3 record with no marker and the continuation shape reads as a continuation.</item>
    /// <item>legacy-text: a beta.3 record with no marker and text reads as an ordinary input.</item>
    /// <item>snapshot-marked: a pending marked continuation keeps its marker through the snapshot.</item>
    /// </list>
    /// Planted bug: the mapper drops the marker, applies the shape rule to a record that this binary wrote, or reads
    /// every record with no marker as a continuation.
    /// </summary>
    [Theory]
    [InlineData("journal-marked", true)]
    [InlineData("journal-empty-person", false)]
    [InlineData("legacy-empty", true)]
    [InlineData("legacy-text", false)]
    [InlineData("snapshot-marked", true)]
    public void The_continuation_marker_survives_the_journal_and_the_snapshot(string row, bool expected)
    {
        // Arrange
        var input = row switch
        {
            "journal-marked" or "snapshot-marked" => Input("") with { ContinuesStoppedTurn = true },
            "legacy-text" => Input("Check the package profile"),
            _ => Input("")
        };

        // Act
        var restored = row switch
        {
            "snapshot-marked" => Assert.Single(SessionState.FromSnapshot(NetclawProtoMapper.FromProto(
                NetclawProtoMapper.ToProtoV2(SessionState.Empty.Apply(input).ToSnapshot()))).PendingInputs),
            "legacy-empty" or "legacy-text" => NetclawProtoMapper.FromProto(ThroughBytes(Beta3Record(input))),
            _ => NetclawProtoMapper.FromProto(ThroughBytes(NetclawProtoMapper.ToProto(input)))
        };

        // Assert
        Assert.Equal(expected, restored.ContinuesStoppedTurn);
        Assert.Equal(input.UserMessage.Content, restored.UserMessage.Content);
    }

    /// <summary>
    /// A compaction snapshot deletes the journal records before it, also each ContinuationAuthorized record. The snapshot
    /// carries the authorized continuation input IDs, so a recovery after a passivation still queues an authorized
    /// continuation behind the pause (#2659 design D3). Rows: authorized (the set survives the snapshot bytes) and
    /// old-snapshot (a snapshot written with no set restores an empty set; a repeated field with no entries has the
    /// bytes of a snapshot from a binary without the field).
    /// Planted bug: the snapshot drops the set, so a passivation after a compaction closes an authorized continuation.
    /// </summary>
    [Theory]
    [InlineData("authorized")]
    [InlineData("old-snapshot")]
    public void The_snapshot_keeps_the_authorized_continuations(string row)
    {
        // Arrange
        var continuation = Input("") with { ContinuesStoppedTurn = true };
        var snapshot = SessionState.Empty.Apply(continuation).ToSnapshot();
        if (row == "authorized")
            snapshot = snapshot with { AuthorizedContinuationInputIds = [continuation.InputId] };

        // Act
        var bytes = NetclawProtoMapper.ToProtoV2(snapshot).ToByteArray();
        var restored = NetclawProtoMapper.FromProto(
            Netclaw.Actors.Serialization.Proto.SessionSnapshotV2Proto.Parser.ParseFrom(bytes));

        // Assert
        Assert.Equal(row == "authorized" ? [continuation.InputId] : Array.Empty<InputId>(),
            restored.AuthorizedContinuationInputIds);
        Assert.Equal(continuation.InputId, Assert.Single(restored.PendingInputs).InputId);
    }

    // The fields that a 0.28.0-beta.3 binary writes for an admitted input. That binary has no continuation marker.
    private static InputAdmittedProto Beta3Record(InputAdmitted input)
    {
        var written = NetclawProtoMapper.ToProto(input);
        return new InputAdmittedProto
        {
            SessionId = written.SessionId,
            InputId = written.InputId,
            UserMessage = written.UserMessage,
            TurnContext = written.TurnContext,
            AdmittedAtMs = written.AdmittedAtMs
        };
    }

    private static InputAdmittedProto ThroughBytes(InputAdmittedProto proto)
        => InputAdmittedProto.Parser.ParseFrom(proto.ToByteArray());

    private static InputAdmitted Input(string content) => new()
    {
        SessionId = Owner,
        InputId = new InputId($"input-{content.Length}"),
        UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = content },
        TurnContext = new TurnContextRecord
        {
            SessionId = Owner, TurnId = "t1", RequesterSenderId = new SenderId("alice"),
            RequesterPrincipal = PrincipalClassification.Operator, Boundary = TrustBoundary.Personal
        },
        AdmittedAtMs = 5
    };
}
