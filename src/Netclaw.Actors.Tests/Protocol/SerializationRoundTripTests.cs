// -----------------------------------------------------------------------
// <copyright file="SerializationRoundTripTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Serialization;
using Google.Protobuf;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Serialization;
using ChildContinuationProto = Netclaw.Actors.Serialization.Proto.ChildContinuationProto;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

public sealed class SerializationRoundTripTests : TestKit
{
    public SerializationRoundTripTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithNetclawSerialization();
    }

    private T RoundTrip<T>(T value)
    {
        var serialization = Sys.Serialization;
        var serializer = serialization.FindSerializerFor(value);
        var bytes = serializer.ToBinary(value);
        var manifest = serializer is SerializerWithStringManifest swm ? swm.Manifest(value) : string.Empty;
        return (T)serialization.Deserialize(bytes, serializer.Identifier, manifest);
    }

    private byte[] Serialize<T>(T value)
        where T : notnull
    {
        var serializer = Sys.Serialization.FindSerializerFor(value);
        return serializer.ToBinary(value);
    }

    // Decodes bytes that a test built by hand, with the serializer that the journal uses.
    private T Deserialize<T>(byte[] bytes, T probe)
        where T : notnull
    {
        var serializer = Sys.Serialization.FindSerializerFor(probe);
        var manifest = serializer is SerializerWithStringManifest swm ? swm.Manifest(probe) : string.Empty;
        return Assert.IsType<T>(Sys.Serialization.Deserialize(bytes, serializer.Identifier, manifest));
    }

    // Appends one field to a serialized message, as a writer that uses another proto file would write it.
    // Protobuf merges a message and a field list that follows it, so the result is one message.
    private static byte[] WithField(byte[] message, Action<CodedOutputStream> writeValue)
    {
        using var stream = new MemoryStream();
        stream.Write(message);
        var output = new CodedOutputStream(stream);
        writeValue(output);
        output.Flush();
        return stream.ToArray();
    }

    private static byte[] WithVarintField(byte[] message, int fieldNumber, bool value)
        => WithField(message, output =>
        {
            output.WriteTag(fieldNumber, WireFormat.WireType.Varint);
            output.WriteBool(value);
        });

    private static byte[] WithStringField(byte[] message, int fieldNumber, string value)
        => WithField(message, output =>
        {
            output.WriteTag(fieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteString(value);
        });

    // A message field of the coordination stack and of #1897 uses these numbers with a length-delimited value.
    private static byte[] WithEmptyMessageField(byte[] message, int fieldNumber)
        => WithField(message, output =>
        {
            output.WriteTag(fieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteLength(0);
        });

    // The journal writes a snapshot as SessionSnapshotV2Proto. Its field 1 is the SessionSnapshotProto state record,
    // which holds the delivery fields. This wraps a hand-built state record the way the current writer does.
    private static byte[] AsSnapshotV2(byte[] stateRecord)
        => WithField([], output =>
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(stateRecord));
        });

    // The state record that the journal writer puts in field 1 of a v2 snapshot.
    private byte[] SnapshotStateRecord(SessionSnapshot snapshot)
        => Serialization.Proto.SessionSnapshotV2Proto.Parser.ParseFrom(Serialize(snapshot)).State.ToByteArray();

    // Reads the mark as nullable. Null means the snapshot predates the field. False is a value.
    private static bool? RoutedMark(SessionSnapshot snapshot) => snapshot.EligibleDeliveryTurnRoutedSkill;

    // The field numbers on the wire, in order. This reads the raw bytes and not the generated class.
    private static List<int> WireFieldNumbers(byte[] message)
    {
        var numbers = new List<int>();
        var input = new CodedInputStream(message);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            numbers.Add(WireFormat.GetTagFieldNumber(tag));
            input.SkipLastField();
        }

        return numbers;
    }

    [Fact]
    public void SessionId_round_trips()
    {
        var original = new SessionId("C99999/1708531200.000100");
        var result = RoundTrip(original);
        Assert.Equal(original, result);
        Assert.Equal("C99999/1708531200.000100", result.Value);
    }

    [Fact]
    public void SendUserMessage_round_trips()
    {
        var original = new SendUserMessage
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            Content = "Hello, Netclaw!"
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(original.Content, result.Content);
    }

    [Fact]
    public void SerializableChatMessage_round_trips_user_message()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = "What is the weather on pi1?"
        };

        var result = RoundTrip(original);

        Assert.Equal(ChatRole.User, result.Role);
        Assert.Equal(original.Content, result.Content);
        Assert.Null(result.Name);
    }

    [Fact]
    public void SerializableChatMessage_round_trips_tool_message()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.Tool,
            Content = "{\"temperature\": 22}",
            Name = "get_weather"
        };

        var result = RoundTrip(original);

        Assert.Equal(ChatRole.Tool, result.Role);
        Assert.Equal(original.Content, result.Content);
        Assert.Equal("get_weather", result.Name);
    }

    [Fact]
    public void TurnRecorded_round_trips()
    {
        var ts = new DateTimeOffset(2026, 2, 21, 10, 1, 0, TimeSpan.Zero);
        var original = new TurnRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            UserMessage = new SerializableChatMessage
            {
                Role = ChatRole.User,
                Content = "Hello"
            },
            AssistantReply = new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "Hi there!"
            },
            RecordedAtMs = ts.ToUnixTimeMilliseconds()
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(ChatRole.User, result.UserMessage.Role);
        Assert.Equal("Hello", result.UserMessage.Content);
        Assert.Equal(ChatRole.Assistant, result.AssistantReply.Role);
        Assert.Equal("Hi there!", result.AssistantReply.Content);
        Assert.Equal(original.RecordedAtMs, result.RecordedAtMs);
    }

    [Fact]
    public void Admitted_input_and_terminal_ids_survive_journal_and_snapshot_round_trips()
    {
        var sessionId = new SessionId("C99999/1708531200.000100");
        var inputId = new InputId("input-1");
        var admitted = new InputAdmitted
        {
            SessionId = sessionId,
            InputId = inputId,
            SourceMessageId = "event-1",
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Continue the task" },
            ExecutableText = "Continue the task",
            TurnContext = new TurnContextRecord
            {
                SessionId = sessionId,
                TurnId = "turn-1",
                Audience = TrustAudience.Personal,
                Boundary = new TrustBoundary("slack:C99999"),
                ChannelType = "slack",
                RequesterSenderId = new SenderId("U123"),
                RequesterPrincipal = PrincipalClassification.Operator
            },
            AdmittedAtMs = 1_700_000_000_000
        };

        var restored = RoundTrip(admitted);
        Assert.Equal(admitted.InputId, restored.InputId);
        Assert.Equal(admitted.UserMessage.Content, restored.UserMessage.Content);
        Assert.Equal(admitted.TurnContext?.Boundary, restored.TurnContext?.Boundary);
        Assert.Equal(admitted.TurnContext?.RequesterSenderId, restored.TurnContext?.RequesterSenderId);

        var snapshot = RoundTrip(new SessionSnapshot
        {
            PendingInputs = [admitted],
            RecentSourceMessageKeys = ["slack:event-1"]
        });
        Assert.Single(snapshot.PendingInputs);
        Assert.Equal(inputId, snapshot.PendingInputs[0].InputId);
        Assert.Equal("slack:event-1", Assert.Single(snapshot.RecentSourceMessageKeys));

        var completed = RoundTrip(new TurnRecorded
        {
            SessionId = sessionId,
            UserMessage = admitted.UserMessage,
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "Done" },
            ConsumedInputIds = [inputId]
        });
        Assert.Equal(inputId, Assert.Single(completed.ConsumedInputIds));

        var closed = RoundTrip(new InputClosed { SessionId = sessionId, InputIds = [inputId] });
        Assert.Equal(inputId, Assert.Single(closed.InputIds));

        var toolStarted = RoundTrip(new ToolBatchStarted
        {
            SessionId = sessionId,
            UserMessage = admitted.UserMessage,
            AssistantMessage = new SerializableChatMessage { Role = ChatRole.Assistant },
            ConsumedInputIds = [inputId]
        });
        Assert.Equal(inputId, Assert.Single(toolStarted.ConsumedInputIds));
    }

    /// <summary>
    /// Round 4 nit 3. InputClosed.KeepsTurn (proto field 8) round-trips as true and as false. A row from an older binary
    /// has no field 8, and it reads false.
    /// </summary>
    [Fact]
    public void InputClosed_keeps_turn_round_trips_and_an_old_row_reads_false()
    {
        // Arrange
        var sessionId = new SessionId("slack/C1/1700.2");
        var inputId = InputId.New();
        var keeps = new InputClosed { SessionId = sessionId, InputIds = [inputId], ClosedAtMs = 42, KeepsTurn = true };
        var ends = keeps with { KeepsTurn = false };
        var oldRow = new Netclaw.Actors.Serialization.Proto.InputClosedProto
        {
            SessionId = new Netclaw.Actors.Serialization.Proto.SessionIdProto { Value = sessionId.Value },
            ClosedAtMs = 42
        };
        oldRow.InputIds.Add(inputId.Value);

        // Act
        var keepsBack = RoundTrip(keeps);
        var endsBack = RoundTrip(ends);
        var oldBack = Deserialize(oldRow.ToByteArray(), keeps);

        // Assert
        Assert.True(keepsBack.KeepsTurn);
        Assert.Equal(inputId, Assert.Single(keepsBack.InputIds));
        Assert.False(endsBack.KeepsTurn);
        Assert.False(oldRow.HasKeepsTurn);
        Assert.False(oldBack.KeepsTurn);
        Assert.Equal(inputId, Assert.Single(oldBack.InputIds));
    }

    [Fact]
    public void TurnRecorded_round_trips_preserving_value_object_source_ids()
    {
        var original = new TurnRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "check PR" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "merged" },
            RecordedAtMs = 1_700_000_000_000,
            SourceReminderId = new ReminderId("check-pr:1712000000000"),
            SourceBackgroundJobId = new BackgroundJobId("bg-job:abc123")
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SourceReminderId, result.SourceReminderId);
        Assert.Equal(original.SourceBackgroundJobId, result.SourceBackgroundJobId);
    }

    [Fact]
    public void TurnRecorded_routed_skill_mark_round_trips_as_a_plain_proto_bool()
    {
        // A routed-skill turn is eligible for the reminder alert but never for a retry on the main model.
        // The mark must survive the journal, because replay rebuilds the delivery state from it.
        var original = new TurnRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "/ops-route check" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "done" },
            RecordedAtMs = 1_700_000_000_000,
            SourceReminderId = new ReminderId("check-pr:1712000000000"),
            RoutedSkill = true
        };

        var expected = NetclawProtoMapper.ToProto(original with { RoutedSkill = false });
        expected.RoutedSkill = true;
        Assert.Equal(expected.ToByteArray(), Serialize(original));

        var result = RoundTrip(original);
        Assert.True(result.RoutedSkill);
        Assert.Equal(original.SourceReminderId, result.SourceReminderId);
    }

    [Fact]
    public void TurnRecorded_from_before_the_routed_mark_reads_as_not_routed()
    {
        // An old journal entry has no routed field. It reads as an ordinary turn, and a turn that is not routed
        // writes the same bytes as before the field existed.
        // Built from an event that never sets the mark, so the proto holds no routed field.
        var oldShape = NetclawProtoMapper.ToProto(new TurnRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "hi" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "hello" },
            RecordedAtMs = 1_700_000_000_000,
            SourceReminderId = new ReminderId("check-pr:1712000000000")
        });

        var restored = NetclawProtoMapper.FromProto(oldShape);

        Assert.False(restored.RoutedSkill);
        Assert.Equal(new ReminderId("check-pr:1712000000000"), restored.SourceReminderId);
        Assert.Equal(oldShape.ToByteArray(), Serialize(restored));
    }

    [Fact]
    public void Tool_result_failure_code_round_trips_through_the_transport_DTO()
    {
        var original = new ToolResultOutput
        {
            SessionId = new SessionId("test/wire"),
            CallId = new ToolCallId("call-rejected"),
            ToolName = new ToolName("search"),
            Result = "The tool was not executed.",
            FailureCode = "invalid_rationale"
        };

        var result = Assert.IsType<ToolResultOutput>(
            SessionOutputDtoMapper.FromDto(SessionOutputDtoMapper.ToDto(original)));

        Assert.Equal("invalid_rationale", result.FailureCode);
    }

    [Fact]
    public void TurnRecorded_round_trips_with_null_source_ids()
    {
        var original = new TurnRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "hi" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "hello" },
            RecordedAtMs = 1_700_000_000_000
        };

        var result = RoundTrip(original);

        Assert.Null(result.SourceReminderId);
        Assert.Null(result.SourceBackgroundJobId);
    }

    [Fact]
    public void TurnRecorded_value_object_source_ids_use_bare_string_proto_fields()
    {
        // Wire-compat: the reminder/background-job value objects map to the SAME
        // bare-string proto fields the pre-value-object code used, so old journals
        // deserialize unchanged and new journals are byte-identical. Proven at the
        // proto-mapper boundary in both directions.
        var evt = new TurnRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "x" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "y" },
            RecordedAtMs = 1_700_000_000_000,
            SourceReminderId = new ReminderId("check-pr:1712000000000"),
            SourceBackgroundJobId = new BackgroundJobId("bg-job:abc123")
        };

        // Forward: value object -> bare string on the wire (no nested object).
        var proto = NetclawProtoMapper.ToProto(evt);
        Assert.Equal("check-pr:1712000000000", proto.SourceReminderId);
        Assert.Equal("bg-job:abc123", proto.SourceBackgroundJobId);

        // Reverse: an "old" proto carrying bare strings deserializes into the
        // value-object-typed event.
        var restored = NetclawProtoMapper.FromProto(proto);
        Assert.Equal(new ReminderId("check-pr:1712000000000"), restored.SourceReminderId);
        Assert.Equal(new BackgroundJobId("bg-job:abc123"), restored.SourceBackgroundJobId);
    }

    [Fact]
    public void SessionCompacted_round_trips_with_messages()
    {
        var ts = new DateTimeOffset(2026, 2, 21, 11, 0, 0, TimeSpan.Zero);
        var original = new SessionCompacted
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            Summary = "The user asked about system status; all services healthy.",
            CompactedMessages =
            [
                new() { Role = ChatRole.System, Content = "Summary: all services healthy." }
            ],
            TurnCountBefore = 42,
            CompactedAtMs = ts.ToUnixTimeMilliseconds()
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(original.Summary, result.Summary);
        Assert.Single(result.CompactedMessages);
        Assert.Equal(ChatRole.System, result.CompactedMessages[0].Role);
        Assert.Equal("Summary: all services healthy.", result.CompactedMessages[0].Content);
        Assert.Equal(42, result.TurnCountBefore);
        Assert.Equal(original.CompactedAtMs, result.CompactedAtMs);
    }

    [Fact]
    public void SerializableChatMessage_round_trips_with_media_references()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = "Check this image",
            MediaReferences =
            [
                new SerializableMediaReference
                {
                    RelativePath = "abc123.png",
                    MimeType = new Netclaw.Media.MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                },
                new SerializableMediaReference
                {
                    RelativePath = "def456.jpg",
                    MimeType = new Netclaw.Media.MimeType("image/jpeg"),
                    Modality = (int)MediaModality.Image
                }
            ]
        };

        var result = RoundTrip(original);

        Assert.Equal(ChatRole.User, result.Role);
        Assert.Equal("Check this image", result.Content);
        Assert.Equal(2, result.MediaReferences.Count);
        Assert.Equal("abc123.png", result.MediaReferences[0].RelativePath);
        Assert.Equal("image/png", result.MediaReferences[0].MimeType.Value);
        Assert.Equal((int)MediaModality.Image, result.MediaReferences[0].Modality);
        Assert.Equal("def456.jpg", result.MediaReferences[1].RelativePath);
        Assert.Equal("image/jpeg", result.MediaReferences[1].MimeType.Value);
    }

    [Fact]
    public void SendUserMessage_round_trips_with_media_references()
    {
        var original = new SendUserMessage
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            Content = "Look at this",
            MediaReferences =
            [
                new SerializableMediaReference
                {
                    RelativePath = "photo.png",
                    MimeType = new Netclaw.Media.MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                }
            ]
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal("Look at this", result.Content);
        Assert.Single(result.MediaReferences);
        Assert.Equal("photo.png", result.MediaReferences[0].RelativePath);
    }

    [Fact]
    public void SerializableChatMessage_round_trips_without_media_references()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = "Just text"
        };

        var result = RoundTrip(original);

        Assert.Equal("Just text", result.Content);
        Assert.Empty(result.MediaReferences);
    }

    [Fact]
    public void WorkingContext_round_trips()
    {
        var original = WorkingContext.Empty
            .AddRecentFile("src/Rect.cs")
            .AddRecentFile("src/Thickness.cs");

        var result = RoundTrip(original);

        Assert.Equal(
            new[] { "src/Thickness.cs", "src/Rect.cs" },
            result.RecentFiles);
    }

    [Fact]
    public void WorkingContext_round_trips_with_project_directory()
    {
        var original = WorkingContext.Empty
            .WithProjectDirectory("/home/user/akadonic")
            .AddRecentFile("src/Rect.cs");

        var result = RoundTrip(original);

        Assert.Equal("/home/user/akadonic", result.ProjectDirectory);
        Assert.Equal(new[] { "src/Rect.cs" }, result.RecentFiles);
    }

    [Fact]
    public void WorkingContext_without_project_directory_deserializes_as_null()
    {
        var original = WorkingContext.Empty
            .AddRecentFile("src/Rect.cs");

        var result = RoundTrip(original);

        Assert.Null(result.ProjectDirectory);
        Assert.Single(result.RecentFiles);
    }

    [Fact]
    public void ReminderPayload_round_trips()
    {
        var original = new ReminderPayload
        {
            Id = new ReminderId("daily-standup")
        };

        var result = RoundTrip(original);

        Assert.Equal("daily-standup", result.Id.Value);
    }

    [Fact]
    public void SerializableChatMessage_round_trips_tool_call_with_MetaJson()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            [
                new SerializableToolCall
                {
                    CallId = new Netclaw.Tools.ToolCallId("call-1"),
                    Name = new Netclaw.Tools.ToolName("shell_execute"),
                    ArgumentsJson = """{"Command":"dotnet test"}""",
                    MetaJson = """{"rationale":"running tests","timeout_seconds":300}"""
                }
            ]
        };

        var result = RoundTrip(original);

        var tc = Assert.Single(result.ToolCalls);
        Assert.Equal("call-1", tc.CallId.Value);
        Assert.Equal("shell_execute", tc.Name.Value);
        Assert.Equal("""{"Command":"dotnet test"}""", tc.ArgumentsJson);
        Assert.Equal("""{"rationale":"running tests","timeout_seconds":300}""", tc.MetaJson);
    }

    [Fact]
    public void SerializableChatMessage_round_trips_tool_call_without_MetaJson_backwards_compat()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            [
                new SerializableToolCall
                {
                    CallId = new Netclaw.Tools.ToolCallId("call-1"),
                    Name = new Netclaw.Tools.ToolName("web_search"),
                    ArgumentsJson = """{"query":"test"}"""
                }
            ]
        };

        var result = RoundTrip(original);

        var tc = Assert.Single(result.ToolCalls);
        Assert.Equal("call-1", tc.CallId.Value);
        Assert.Equal("web_search", tc.Name.Value);
        Assert.Null(tc.MetaJson);
    }

    [Fact]
    public void AdoptedContextRecorded_round_trips()
    {
        var original = new AdoptedContextRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            AuthorizedMessageId = "msg-42",
            AuthorizerSenderId = new SenderId("U12345"),
            LowerBound = "msg-40",
            UpperBound = "msg-42",
            Projection = "Alice said hello; Bob replied",
            HasAdoptedContext = true,
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = ["U12345", "U67890"],
            ProjectionPersisted = true,
            RecordedAtMs = 1708531200000,
            Messages =
            [
                new AdoptedContextRecorded.AdoptedMessageRecord
                {
                    MessageId = "msg-41",
                    SenderId = new SenderId("U67890"),
                    TimestampMs = 1708531190000,
                    AuthorityAtInclusion = "verified"
                }
            ]
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal("msg-42", result.AuthorizedMessageId);
        Assert.Equal("U12345", result.AuthorizerSenderId?.Value);
        Assert.Equal("msg-40", result.LowerBound);
        Assert.Equal("msg-42", result.UpperBound);
        Assert.Equal("Alice said hello; Bob replied", result.Projection);
        Assert.True(result.HasAdoptedContext);
        Assert.True(result.HasThirdPartyAdoptedContext);
        Assert.Equal(["U12345", "U67890"], result.AdoptedSpeakerIds);
        Assert.True(result.ProjectionPersisted);
        Assert.Equal(1708531200000, result.RecordedAtMs);
        var msg = Assert.Single(result.Messages);
        Assert.Equal("msg-41", msg.MessageId);
        Assert.Equal("U67890", msg.SenderId.Value);
        Assert.Equal(1708531190000, msg.TimestampMs);
        Assert.Equal("verified", msg.AuthorityAtInclusion);
    }

    [Fact]
    public void AdoptedContextRecorded_round_trips_with_null_optionals()
    {
        var original = new AdoptedContextRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            AuthorizedMessageId = "msg-1",
            AuthorizerSenderId = null,
            LowerBound = null,
            UpperBound = null,
            Projection = "",
            HasAdoptedContext = false,
            HasThirdPartyAdoptedContext = false,
            ProjectionPersisted = false,
            RecordedAtMs = 1708531200000
        };

        var result = RoundTrip(original);

        Assert.Null(result.AuthorizerSenderId);
        Assert.Null(result.LowerBound);
        Assert.Null(result.UpperBound);
        Assert.False(result.HasAdoptedContext);
        Assert.False(result.HasThirdPartyAdoptedContext);
        Assert.Empty(result.AdoptedSpeakerIds);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void CursorAdvanced_round_trips()
    {
        var original = new CursorAdvanced("1778082564.879599");
        var result = RoundTrip(original);
        Assert.Equal(original, result);
        Assert.Equal("1778082564.879599", result.Cursor);
    }

    [Fact]
    public void SessionBackgroundJobsReaped_round_trips()
    {
        var original = new SessionBackgroundJobsReaped
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            ReapedAtMs = 1_778_082_564_879
        };
        var result = RoundTrip(original);
        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(original.ReapedAtMs, result.ReapedAtMs);
    }

    [Fact]
    public void PendingApprovalPromptTracked_round_trips_with_tool_name_and_display_text()
    {
        var original = new PendingApprovalPromptTracked
        {
            CallId = "call-shell-1",
            RequesterSenderId = "U-requester",
            RequesterPrincipal = Netclaw.Configuration.PrincipalClassification.TrustedInternal,
            OptionKeys = ["approve_once", "deny"],
            PromptId = "1779898078.594569",
            ToolName = "shell_execute",
            DisplayText = "gh pr create --base master --head feature/foo"
        };

        var result = RoundTrip(original);

        Assert.Equal(original.CallId, result.CallId);
        Assert.Equal(original.RequesterSenderId, result.RequesterSenderId);
        Assert.Equal(original.RequesterPrincipal, result.RequesterPrincipal);
        Assert.Equal(original.OptionKeys, result.OptionKeys);
        Assert.Equal(original.PromptId, result.PromptId);
        Assert.Equal(original.ToolName, result.ToolName);
        Assert.Equal(original.DisplayText, result.DisplayText);
    }

    [Fact]
    public void PendingApprovalPromptTracked_round_trips_when_tool_name_and_display_text_are_null()
    {
        // Backward-compat with pre-0.21.1 journals: the new fields are optional
        // and a recovered record must come back with nulls intact so the binding
        // falls through to the generic cold-spawn banner.
        var original = new PendingApprovalPromptTracked
        {
            CallId = "call-legacy",
            RequesterSenderId = null,
            RequesterPrincipal = null,
            OptionKeys = ["approve_once"],
            PromptId = "1779898078.594569",
            ToolName = null,
            DisplayText = null
        };

        var result = RoundTrip(original);

        Assert.Null(result.ToolName);
        Assert.Null(result.DisplayText);
        Assert.Equal(original.PromptId, result.PromptId);
    }

    [Fact]
    public void Unknown_manifest_throws_on_deserialize()
    {
        var serializer = new Serialization.NetclawProtobufSerializer((Akka.Actor.ExtendedActorSystem)Sys);
        var bytes = new byte[] { 0x08, 0x01 };

        var ex = Record.Exception(() => serializer.FromBinary(bytes, "unknown-manifest-v99"));

        Assert.NotNull(ex);
        Assert.Contains("Unknown manifest", ex.Message);
    }

    [Fact]
    public void Unregistered_type_throws_on_serialize()
    {
        var serialization = Sys.Serialization;

        // UnregisteredMessage is NOT in the boundTypes list for NetclawProtobufSerializer,
        // so strict serialization mode should throw instead of falling back to JSON.
        Assert.Throws<System.Runtime.Serialization.SerializationException>(
            () => serialization.FindSerializerFor(new UnregisteredMessage("test")));
    }

    private sealed record UnregisteredMessage(string Value);

    [Fact]
    public void MemoriesDistilledV2_round_trips()
    {
        // Regression: this type was added to SessionMemoryObserverActor but
        // its serialization binding, proto message, and ProtoMapper entries
        // were missed — production sessions hit "No serializer binding
        // found for type MemoriesDistilledV2" three times in a four-minute
        // window before the gap was caught. Strict serialization now
        // refuses to fall back to JSON, so the gap manifests at
        // Persist() time rather than silently writing schema-drift bytes.
        var original = new MemoriesDistilledV2(
            Anchors: ["alpha-anchor", "beta-anchor"],
            Proposals:
            [
                new ProposedMemoryContext("alpha-anchor", "Alpha title", "Alpha content body."),
                new ProposedMemoryContext("beta-anchor", "Beta title", "Beta content body.")
            ],
            TimestampMs: 1715520000000L);

        var result = RoundTrip(original);

        Assert.Equal(original.Anchors, result.Anchors);
        Assert.Equal(original.Proposals.Count, result.Proposals.Count);
        for (var i = 0; i < original.Proposals.Count; i++)
        {
            Assert.Equal(original.Proposals[i].Anchor, result.Proposals[i].Anchor);
            Assert.Equal(original.Proposals[i].Title, result.Proposals[i].Title);
            Assert.Equal(original.Proposals[i].Content, result.Proposals[i].Content);
        }
        Assert.Equal(original.TimestampMs, result.TimestampMs);
    }

    // ── Value-object wrap byte-equality (issue #994 Pass 7b) ──
    //
    // Pass 7b routes ToolCallId / ToolName / MimeType / BackgroundJobId value
    // objects through protobuf-registered records. The wire bytes MUST stay
    // byte-identical to the pre-wrap (raw-primitive) representation: a daemon
    // running the new binary has to read journal entries written by the old
    // one and vice versa. Each test below builds the proto message by hand
    // with the bare primitive — exactly what the pre-wrap mapper emitted —
    // and asserts the value-object record serializes to the same bytes.

    [Fact]
    public void SerializableToolCall_wrap_is_byte_identical_to_raw_primitive_proto()
    {
        var wrapped = new SerializableToolCall
        {
            CallId = new Netclaw.Tools.ToolCallId("call-77"),
            Name = new Netclaw.Tools.ToolName("shell_execute"),
            ArgumentsJson = """{"Command":"ls"}""",
            MetaJson = """{"rationale":"list"}"""
        };

        var expected = new Serialization.Proto.SerializableToolCallProto
        {
            CallId = "call-77",
            Name = "shell_execute",
            ArgumentsJson = """{"Command":"ls"}""",
            MetaJson = """{"rationale":"list"}"""
        }.ToByteArray();

        Assert.Equal(expected, Serialize(wrapped));

        var result = RoundTrip(wrapped);
        Assert.Equal(new Netclaw.Tools.ToolCallId("call-77"), result.CallId);
        Assert.Equal(new Netclaw.Tools.ToolName("shell_execute"), result.Name);
    }

    [Fact]
    public void SerializableMediaReference_wrap_is_byte_identical_to_raw_primitive_proto()
    {
        var wrapped = new SerializableMediaReference
        {
            RelativePath = "photo.png",
            MimeType = new Netclaw.Media.MimeType("image/png"),
            Modality = (int)MediaModality.Image,
            FileSizeBytes = 4096
        };

        var expected = new Serialization.Proto.SerializableMediaReferenceProto
        {
            RelativePath = "photo.png",
            MimeType = "image/png",
            Modality = (int)MediaModality.Image,
            FileSizeBytes = 4096
        }.ToByteArray();

        Assert.Equal(expected, Serialize(wrapped));

        var result = RoundTrip(wrapped);
        Assert.Equal(new Netclaw.Media.MimeType("image/png"), result.MimeType);
    }

    [Fact]
    public void SerializableChatMessage_tool_call_id_wrap_is_byte_identical_to_raw_primitive_proto()
    {
        var wrapped = new SerializableChatMessage
        {
            Role = ChatRole.Tool,
            Content = "{\"ok\":true}",
            Name = "shell_execute",
            ToolCallId = new Netclaw.Tools.ToolCallId("call-88")
        };

        var expected = new Serialization.Proto.SerializableChatMessageProto
        {
            Role = (Serialization.Proto.ChatRole)(int)ChatRole.Tool,
            Content = "{\"ok\":true}",
            Name = "shell_execute",
            ToolCallId = "call-88"
        }.ToByteArray();

        Assert.Equal(expected, Serialize(wrapped));

        var result = RoundTrip(wrapped);
        Assert.Equal(new Netclaw.Tools.ToolCallId("call-88"), result.ToolCallId);
    }

    [Fact]
    public void SessionSnapshot_active_job_id_wrap_is_byte_identical_to_raw_primitive_proto()
    {
        var wrapped = new SessionSnapshot
        {
            TurnCount = 1,
            History = [],
            ActiveBackgroundJobs =
            [
                new Netclaw.Actors.Jobs.ActiveJobInfo
                {
                    JobId = new Netclaw.Actors.Jobs.BackgroundJobId("job-55"),
                    Command = "dotnet build",
                    Rationale = "compile",
                    StartedAtMs = 1715520000000L,
                    Audience = Netclaw.Configuration.TrustAudience.Team,
                    Boundary = Netclaw.Configuration.TrustBoundary.Team
                }
            ]
        };

        var expected = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 1,
            ActiveBackgroundJobs =
            {
                new Serialization.Proto.ActiveJobInfoProto
                {
                    JobId = "job-55",
                    Command = "dotnet build",
                    Rationale = "compile",
                    StartedAtMs = 1715520000000L,
                    Audience = (Serialization.Proto.TrustAudience)(int)Netclaw.Configuration.TrustAudience.Team,
                    Boundary = Netclaw.Configuration.TrustBoundary.TeamValue
                }
            }
        }.ToByteArray();

        Assert.Equal(expected, SerializeLegacySnapshot(wrapped));

        var legacyResult = DeserializeLegacySnapshot(expected);
        Assert.Equal(new Netclaw.Actors.Jobs.BackgroundJobId("job-55"), legacyResult.ActiveBackgroundJobs[0].JobId);

        var result = RoundTrip(wrapped);
        Assert.Equal(new Netclaw.Actors.Jobs.BackgroundJobId("job-55"), result.ActiveBackgroundJobs[0].JobId);
    }

    [Fact]
    public void SessionSnapshot_eligible_turn_number_wrap_is_byte_identical_to_raw_primitive_proto()
    {
        // The v1 snapshot field stays a primitive int32 after the value wrap.
        var wrapped = new SessionSnapshot
        {
            TurnCount = 7,
            History = [],
            EligibleDeliveryTurnNumber = new TurnNumber(7)
        };

        var expected = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 7,
            EligibleDeliveryTurnNumber = 7
        }.ToByteArray();

        Assert.Equal(expected, SerializeLegacySnapshot(wrapped));

        var legacyResult = DeserializeLegacySnapshot(expected);
        Assert.Equal(new TurnNumber(7), legacyResult.EligibleDeliveryTurnNumber);

        var result = RoundTrip(wrapped);
        Assert.Equal(new TurnNumber(7), result.EligibleDeliveryTurnNumber);
    }

    [Fact]
    public void SessionSnapshot_null_eligible_turn_number_omits_proto_field()
    {
        // A null v1 EligibleDeliveryTurnNumber leaves the optional field unset.
        var wrapped = new SessionSnapshot
        {
            TurnCount = 3,
            History = [],
            EligibleDeliveryTurnNumber = null
        };

        var expected = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 3
        }.ToByteArray();

        Assert.Equal(expected, SerializeLegacySnapshot(wrapped));

        Assert.Null(DeserializeLegacySnapshot(expected).EligibleDeliveryTurnNumber);

        var result = RoundTrip(wrapped);
        Assert.Null(result.EligibleDeliveryTurnNumber);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void SessionSnapshot_v2_child_initial_snapshot_requires_a_complete_pair(
        bool lifecycleNeedsSnapshot,
        bool hasWorkingContext,
        bool hasGit,
        bool shouldRestore)
    {
        // Arrange
        var initialSnapshot = new WorkingContextSnapshot
        {
            WorkingContext = WorkingContext.Empty,
            Git = new GitWorkingContextInspection.Skipped()
        };
        var run = new BackgroundChildRun
        {
            RunId = new SubAgentRunId("run-1"),
            ScopeId = new SubAgentScopeId("scope-1"),
            StartKey = new ChildRunStartKey.Tool(new SessionId("session-1"), "turn-1", new ToolCallId("call-1")),
            AgentName = new AgentName("worker"),
            SourceOperation = "spawn_agent",
            ArgumentsDigest = "digest-1",
            OriginalContext = new TurnContextRecord
            {
                SessionId = new SessionId("session-1"),
                TurnId = "turn-1",
                Boundary = TrustBoundary.Public,
                RequesterPrincipal = PrincipalClassification.UntrustedExternal
            },
            InitialWorkingSnapshot = initialSnapshot,
            AcceptedAtMs = 1,
            Lifecycle = new ChildRunLifecycle.Accepted(null),
            SourceReminderId = null
        };
        var proto = NetclawProtoMapper.ToProtoV2(new SessionSnapshot { ChildRuns = [run] });
        var child = Assert.Single(proto.ChildRuns);
        if (!lifecycleNeedsSnapshot)
            child.Closed = new Serialization.Proto.ChildRunClosedV2Proto
            {
                DeliveryCallId = "delivery-1",
                Outcome = (int)SubAgentRunOutcome.Completed
            };
        if (!hasWorkingContext) child.InitialWorkingContext = null!;
        if (!hasGit) child.InitialGit = null!;

        // Act and Assert
        if (!shouldRestore)
        {
            Assert.Throws<InvalidDataException>(() => NetclawProtoMapper.FromProto(proto));
            return;
        }

        var restored = NetclawProtoMapper.FromProto(proto);
        var restoredRun = Assert.Single(restored.ChildRuns);
        Assert.Equal(lifecycleNeedsSnapshot, restoredRun.InitialWorkingSnapshot is not null);
    }

    [Fact]
    public void SessionSnapshot_eligible_reminder_id_round_trips_as_an_optional_proto_string()
    {
        // The snapshot keeps the fire key of the reminder that started the eligible turn. A session that
        // restores from this snapshot does not replay the TurnRecorded event, so the key must travel here.
        var wrapped = new SessionSnapshot
        {
            TurnCount = 7,
            History = [],
            EligibleDeliveryTurnNumber = new TurnNumber(7),
            EligibleDeliveryReminderId = new ReminderId("check-pr:1712000000000")
        };

        var expected = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 7,
            EligibleDeliveryTurnNumber = 7,
            EligibleDeliveryReminderId = "check-pr:1712000000000"
        }.ToByteArray();

        Assert.Equal(expected, SnapshotStateRecord(wrapped));

        var result = RoundTrip(wrapped);
        Assert.Equal(new ReminderId("check-pr:1712000000000"), result.EligibleDeliveryReminderId);
        Assert.Equal(new TurnNumber(7), result.EligibleDeliveryTurnNumber);
    }

    [Fact]
    public void SessionSnapshot_eligible_turn_routed_mark_round_trips_as_a_plain_proto_bool()
    {
        // A session that restores from a snapshot does not replay TurnRecorded, so the snapshot carries the
        // routed mark of the eligible turn. Without it a restored session would retry a routed turn.
        var wrapped = new SessionSnapshot
        {
            TurnCount = 7,
            History = [],
            EligibleDeliveryTurnNumber = new TurnNumber(7),
            EligibleDeliveryReminderId = new ReminderId("check-pr:1712000000000"),
            EligibleDeliveryTurnRoutedSkill = true
        };

        var expected = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 7,
            EligibleDeliveryTurnNumber = 7,
            EligibleDeliveryReminderId = "check-pr:1712000000000",
            EligibleDeliveryTurnRoutedSkill = true
        }.ToByteArray();

        Assert.Equal(expected, SnapshotStateRecord(wrapped));
        Assert.True(RoundTrip(wrapped).EligibleDeliveryTurnRoutedSkill);
    }

    [Fact]
    public void SessionSnapshot_from_before_the_reminder_field_loads_with_no_reminder()
    {
        // Old journals hold snapshots without the delivery fields. They must load, with the eligible turn kept
        // and no reminder. A session never invents a reminder for such a snapshot.
        var oldShape = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 4,
            EligibleDeliveryTurnNumber = 4
        }.ToByteArray();

        // A journal from before the delivery fields holds v1 snapshots (manifest "ss-v1").
        var snapshot = DeserializeLegacySnapshot(oldShape);

        Assert.Equal(new TurnNumber(4), snapshot.EligibleDeliveryTurnNumber);
        Assert.Null(snapshot.EligibleDeliveryReminderId);
        // No value is not the same as false. A snapshot without the mark was written before the delivery fields.
        Assert.Null(RoutedMark(snapshot));
        Assert.Equal(oldShape, SnapshotStateRecord(snapshot));
    }

    // --- Review round 3, S2: the field numbers are durable, and other branches use the low numbers ---
    // #2464 (the coordination stack) writes TurnRecordedProto 8 (child_continuation) and SessionSnapshotProto 13 to 19,
    // and it reserves 12. #1897 writes TurnRecordedProto 8 and 9 and SessionSnapshotProto 12. A daemon built from
    // such a branch can hold that data. These tests fix the numbers, so a later collision fails here.

    private const int TurnRecordedRoutedSkillField = 10;
    private const int SnapshotEligibleReminderField = 20;
    private const int SnapshotEligibleRoutedSkillField = 21;

    [Fact]
    public void The_delivery_fields_use_numbers_that_no_other_branch_uses()
    {
        var routed = Serialization.Proto.TurnRecordedProto.Descriptor.FindFieldByName("routed_skill");
        var reminder = Serialization.Proto.SessionSnapshotProto.Descriptor.FindFieldByName("eligible_delivery_reminder_id");
        var snapshotRouted = Serialization.Proto.SessionSnapshotProto.Descriptor.FindFieldByName("eligible_delivery_turn_routed_skill");

        Assert.Equal(TurnRecordedRoutedSkillField, routed.FieldNumber);
        Assert.Equal(SnapshotEligibleReminderField, reminder.FieldNumber);
        Assert.Equal(SnapshotEligibleRoutedSkillField, snapshotRouted.FieldNumber);

        // Explicit presence tells "false" from "absent". An old snapshot has no value.
        Assert.True(snapshotRouted.HasPresence);
        Assert.True(reminder.HasPresence);
    }

    [Fact]
    public void TurnRecorded_reads_the_routed_mark_from_field_10_and_ignores_the_other_branches_fields()
    {
        var probe = new TurnRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "hi" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "hello" },
            RecordedAtMs = 1_700_000_000_000,
            SourceReminderId = new ReminderId("check-pr:1712000000000")
        };
        var oldBytes = Serialize(probe);

        // A hand-built entry with the routed mark at its tag.
        var routed = Deserialize(WithVarintField(oldBytes, TurnRecordedRoutedSkillField, true), probe);
        Assert.True(routed.RoutedSkill);
        Assert.Equal(probe.SourceReminderId, routed.SourceReminderId);

        // Field 8 is the child continuation of the coordination stack. An entry that holds a child continuation
        // at 8 is not a routed mark, and the routed mark at 10 does not change the continuation. No writer sets the
        // continuation any more, so the deployed entry is built by hand.
        var continuation = new ChildContinuation(new SubAgentRunId("run-1"), new ToolCallId("call-1"));
        var deployedEntry = WithField(oldBytes, output =>
        {
            output.WriteTag(8, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(new ChildContinuationProto { RunId = "run-1", DeliveryCallId = "call-1" }.ToByteString());
        });
        var stackEntry = Deserialize(deployedEntry, probe);
        Assert.False(stackEntry.RoutedSkill);
        Assert.Equal(continuation, stackEntry.ChildContinuation);
        var routedStackEntry = Deserialize(WithVarintField(deployedEntry, TurnRecordedRoutedSkillField, true), probe);
        Assert.True(routedStackEntry.RoutedSkill);
        Assert.Equal(continuation, routedStackEntry.ChildContinuation);

        // #1897 writes a message at 9. It is not a routed mark.
        var tuiEntry = Deserialize(WithEmptyMessageField(oldBytes, 9), probe);
        Assert.False(tuiEntry.RoutedSkill);

        // Even a varint at 8 or 9 is not the routed mark.
        Assert.False(Deserialize(WithVarintField(oldBytes, 8, true), probe).RoutedSkill);
        Assert.False(Deserialize(WithVarintField(oldBytes, 9, true), probe).RoutedSkill);
    }

    [Fact]
    public void TurnRecorded_writes_the_routed_mark_at_field_10()
    {
        var routed = new TurnRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "hi" },
            AssistantReply = new SerializableChatMessage { Role = ChatRole.Assistant, Content = "hello" },
            RecordedAtMs = 1_700_000_000_000,
            RoutedSkill = true
        };

        var numbers = WireFieldNumbers(Serialize(routed));

        Assert.Contains(TurnRecordedRoutedSkillField, numbers);
        Assert.DoesNotContain(8, numbers);
        Assert.DoesNotContain(9, numbers);
    }

    [Fact]
    public void SessionSnapshot_reads_the_delivery_fields_from_fields_20_and_21()
    {
        var probe = new SessionSnapshot { TurnCount = 4, History = [] };
        var oldBytes = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 4,
            EligibleDeliveryTurnNumber = 4
        }.ToByteArray();

        var bytes = WithVarintField(
            WithStringField(oldBytes, SnapshotEligibleReminderField, "check-pr:1712000000000"),
            SnapshotEligibleRoutedSkillField,
            true);
        var snapshot = Deserialize(AsSnapshotV2(bytes), probe);

        Assert.Equal(new ReminderId("check-pr:1712000000000"), snapshot.EligibleDeliveryReminderId);
        Assert.True(snapshot.EligibleDeliveryTurnRoutedSkill);
        Assert.Equal(new TurnNumber(4), snapshot.EligibleDeliveryTurnNumber);
    }

    [Fact]
    public void SessionSnapshot_reads_an_explicit_false_routed_mark_as_false_not_as_absent()
    {
        var probe = new SessionSnapshot { TurnCount = 4, History = [] };
        var oldBytes = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 4,
            EligibleDeliveryTurnNumber = 4
        }.ToByteArray();

        // The writer sets the mark for every new snapshot, so a person's turn writes an explicit false.
        var newFormat = Deserialize(AsSnapshotV2(WithVarintField(oldBytes, SnapshotEligibleRoutedSkillField, false)), probe);
        var oldFormat = DeserializeLegacySnapshot(oldBytes);

        Assert.True(RoutedMark(newFormat) is false);
        Assert.Null(RoutedMark(oldFormat));
        Assert.Null(newFormat.EligibleDeliveryReminderId);
    }

    [Fact]
    public void SessionSnapshot_writes_an_explicit_false_routed_mark()
    {
        var snapshot = new SessionSnapshot
        {
            TurnCount = 7,
            History = [],
            EligibleDeliveryTurnNumber = new TurnNumber(7),
            EligibleDeliveryTurnRoutedSkill = false
        };

        var numbers = WireFieldNumbers(SnapshotStateRecord(snapshot));

        // The explicit false is on the wire. An old snapshot lacks it.
        Assert.Contains(SnapshotEligibleRoutedSkillField, numbers);
        Assert.True(RoutedMark(RoundTrip(snapshot)) is false);
    }

    [Fact]
    public void SessionSnapshot_writes_the_delivery_fields_at_fields_20_and_21_and_not_at_12_or_13()
    {
        var snapshot = new SessionSnapshot
        {
            TurnCount = 7,
            History = [],
            EligibleDeliveryTurnNumber = new TurnNumber(7),
            EligibleDeliveryReminderId = new ReminderId("check-pr:1712000000000"),
            EligibleDeliveryTurnRoutedSkill = true
        };

        var numbers = WireFieldNumbers(SnapshotStateRecord(snapshot));

        Assert.Contains(SnapshotEligibleReminderField, numbers);
        Assert.Contains(SnapshotEligibleRoutedSkillField, numbers);
        Assert.DoesNotContain(12, numbers);
        Assert.DoesNotContain(13, numbers);
    }

    [Fact]
    public void SessionSnapshot_ignores_numbers_that_the_coordination_stack_and_1897_use()
    {
        var probe = new SessionSnapshot { TurnCount = 4, History = [] };
        var oldBytes = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 4,
            EligibleDeliveryTurnNumber = 4
        }.ToByteArray();

        // #1897 writes a message at 12 (recent_transcript). This tree reserves 12, and 13 is the loop_admission field
        // of the coordination stack. Neither is a delivery field, and a snapshot with them must still load.
        var bytes = WithEmptyMessageField(WithEmptyMessageField(oldBytes, 12), 13);
        var snapshot = Deserialize(AsSnapshotV2(bytes), probe);

        Assert.Null(snapshot.EligibleDeliveryReminderId);
        Assert.Null(RoutedMark(snapshot));
        Assert.Equal(new TurnNumber(4), snapshot.EligibleDeliveryTurnNumber);

        // Even a string at 12 or a varint at 13 is not a delivery field.
        var stringAndVarint = WithVarintField(WithStringField(oldBytes, 12, "check-pr:1712000000000"), 13, true);
        var other = Deserialize(AsSnapshotV2(stringAndVarint), probe);
        Assert.Null(other.EligibleDeliveryReminderId);
        Assert.Null(RoutedMark(other));
    }

    [Fact]
    public void ToolApprovalRequested_round_trips_all_persisted_context()
    {
        var assignmentDigest = new Netclaw.Configuration.ApprovalAssignmentDigest(
            $"sha256:{new string('a', 64)}");
        var wrapped = new ToolApprovalRequested
        {
            SessionId = new SessionId("C123/1700000000.000001"),
            CallId = "call-pending-1",
            AuthorizationAttemptId = "auth-0123456789abcdef0123456789abcdef",
            ToolName = "shell_execute",
            Patterns = ["git status", "ls"],
            CandidateVerbs = ["git", "ls"],
            Audience = Netclaw.Configuration.TrustAudience.Team,
            Boundary = Netclaw.Configuration.TrustBoundary.Team,
            ChannelType = "slack",
            SupportsInteractiveApproval = true,
            RequesterSenderId = new SenderId("U12345"),
            RequesterPrincipal = Netclaw.Configuration.PrincipalClassification.Operator,
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = ["U12345", "U-observer"],
            Cwd = "/work/repository",
            RepositoryCommonDirectory = "/work/repository/.git",
            ManagedTemporaryDirectory = "/work/session/tmp/parent",
            OptionKeys =
            [
                ApprovalOptionKeys.ApproveOnce,
                ApprovalOptionKeys.ApproveAssignmentEverywhereV1,
                ApprovalOptionKeys.Deny,
            ],
            Candidates =
            [
                new Netclaw.Security.ApprovalCandidate(
                    "git",
                    "/work/repository")
                {
                    AssignmentDigest = assignmentDigest,
                    Shell = Netclaw.Configuration.ApprovalShell.Bash,
                    VerbTokens = Array.AsReadOnly(["git", "push"]),
                },
                new Netclaw.Security.ApprovalCandidate(
                    "ls",
                    null)
            ],
            TurnContext = new TurnContextRecord
            {
                SessionId = new SessionId("C123/1700000000.000001"),
                TurnId = "turn-approval-1",
                Audience = Netclaw.Configuration.TrustAudience.Team,
                Boundary = Netclaw.Configuration.TrustBoundary.Team,
                ChannelType = "slack",
                RequesterSenderId = new SenderId("U12345"),
                RequesterPrincipal = Netclaw.Configuration.PrincipalClassification.Operator,
                TransportAuthenticity = Netclaw.Configuration.TransportAuthenticity.Verified,
                PayloadTaint = Netclaw.Configuration.PayloadTaint.Community,
                SourceScope = "slack-workspace:T123",
                SourceKind = "slack",
                DefaultDeliveryTarget = new ChannelDeliveryTargetInfo(
                    "slack",
                    "destination",
                    "C123",
                    "#operations",
                    "1700000000.000001"),
                RequestedDeliveryTarget = new ChannelDeliveryTargetInfo(
                    "mattermost",
                    "direct_message",
                    "user1234567890123456789012",
                    "@requester"),
                HasAdoptedContext = true,
                HasThirdPartyAdoptedContext = true,
                AdoptedSpeakerIds = ["U12345", "U-observer"],
                SupportsInteractiveApproval = true
            },
            RequestedAtMs = 1700000000000
        };

        var result = RoundTrip(wrapped);

        Assert.Equal(wrapped.SessionId, result.SessionId);
        Assert.Equal(wrapped.CallId, result.CallId);
        Assert.Equal(wrapped.AuthorizationAttemptId, result.AuthorizationAttemptId);
        Assert.Equal(wrapped.ToolName, result.ToolName);
        Assert.Equal(wrapped.Patterns, result.Patterns);
        Assert.Equal(wrapped.CandidateVerbs, result.CandidateVerbs);
        Assert.Equal(wrapped.Audience, result.Audience);
        Assert.Equal(wrapped.Boundary, result.Boundary);
        Assert.Equal(wrapped.ChannelType, result.ChannelType);
        Assert.Equal(wrapped.SupportsInteractiveApproval, result.SupportsInteractiveApproval);
        Assert.Equal(wrapped.RequesterSenderId, result.RequesterSenderId);
        Assert.Equal(wrapped.RequesterPrincipal, result.RequesterPrincipal);
        Assert.Equal(wrapped.HasThirdPartyAdoptedContext, result.HasThirdPartyAdoptedContext);
        Assert.Equal(wrapped.AdoptedSpeakerIds, result.AdoptedSpeakerIds);
        Assert.Equal(wrapped.Cwd, result.Cwd);
        Assert.Equal(wrapped.RepositoryCommonDirectory, result.RepositoryCommonDirectory);
        Assert.Null(result.SessionScratchDirectory);
        Assert.Equal(wrapped.ManagedTemporaryDirectory, result.ManagedTemporaryDirectory);
        Assert.Equal(wrapped.OptionKeys, result.OptionKeys);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("git", result.Candidates[0].Verb);
        Assert.Equal("/work/repository", result.Candidates[0].Directory);
        Assert.Equal(Netclaw.Configuration.ApprovalShell.Bash, result.Candidates[0].Shell);
        Assert.Equal(["git", "push"], result.Candidates[0].VerbTokens);
        Assert.Equal(
            assignmentDigest,
            result.Candidates[0].AssignmentDigest);
        Assert.Equal("ls", result.Candidates[1].Verb);
        Assert.Null(result.Candidates[1].Directory);
        Assert.Null(result.Candidates[1].Shell);
        Assert.Null(result.Candidates[1].VerbTokens);
        Assert.NotNull(result.TurnContext);
        Assert.Equal(wrapped.TurnContext.SessionId, result.TurnContext.SessionId);
        Assert.Equal(wrapped.TurnContext.TurnId, result.TurnContext.TurnId);
        Assert.Equal(wrapped.TurnContext.Audience, result.TurnContext.Audience);
        Assert.Equal(wrapped.TurnContext.Boundary, result.TurnContext.Boundary);
        Assert.Equal(wrapped.TurnContext.ChannelType, result.TurnContext.ChannelType);
        Assert.Equal(wrapped.TurnContext.RequesterSenderId, result.TurnContext.RequesterSenderId);
        Assert.Equal(wrapped.TurnContext.RequesterPrincipal, result.TurnContext.RequesterPrincipal);
        Assert.Equal(wrapped.TurnContext.TransportAuthenticity, result.TurnContext.TransportAuthenticity);
        Assert.Equal(wrapped.TurnContext.PayloadTaint, result.TurnContext.PayloadTaint);
        Assert.Equal(wrapped.TurnContext.SourceScope, result.TurnContext.SourceScope);
        Assert.Equal(wrapped.TurnContext.SourceKind, result.TurnContext.SourceKind);
        Assert.Equal(wrapped.TurnContext.DefaultDeliveryTarget, result.TurnContext.DefaultDeliveryTarget);
        Assert.Equal(wrapped.TurnContext.RequestedDeliveryTarget, result.TurnContext.RequestedDeliveryTarget);
        Assert.Equal(wrapped.TurnContext.HasAdoptedContext, result.TurnContext.HasAdoptedContext);
        Assert.Equal(wrapped.TurnContext.HasThirdPartyAdoptedContext, result.TurnContext.HasThirdPartyAdoptedContext);
        Assert.Equal(wrapped.TurnContext.AdoptedSpeakerIds, result.TurnContext.AdoptedSpeakerIds);
        Assert.Equal(wrapped.TurnContext.SupportsInteractiveApproval, result.TurnContext.SupportsInteractiveApproval);
        Assert.Equal(wrapped.RequestedAtMs, result.RequestedAtMs);
    }

    [Fact]
    public void Assignment_prompt_wire_form_fails_closed_for_a_legacy_reader()
    {
        var digest = new Netclaw.Configuration.ApprovalAssignmentDigest(
            $"sha256:{new string('a', 64)}");
        var current = new ToolApprovalRequested
        {
            SessionId = new SessionId("session-1"),
            CallId = "call-assignment",
            ToolName = "shell_execute",
            Cwd = "/work/repository",
            OptionKeys =
            [
                ApprovalOptionKeys.ApproveOnce,
                ApprovalOptionKeys.ApproveAssignmentSessionV1,
                ApprovalOptionKeys.ApproveAssignmentAlwaysV1,
                ApprovalOptionKeys.ApproveAssignmentRepositoryV1,
                ApprovalOptionKeys.ApproveAssignmentEverywhereV1,
                ApprovalOptionKeys.Deny,
            ],
            Candidates =
            [
                new Netclaw.Security.ApprovalCandidate(
                    "inspect",
                    "/work/repository")
                {
                    AssignmentDigest = digest,
                    Shell = Netclaw.Configuration.ApprovalShell.Bash,
                    VerbTokens = ["inspect"],
                },
            ],
            RequestedAtMs = 1700000000000,
        };

        var wire = Serialization.Proto.ToolApprovalRequestedProto.Parser.ParseFrom(
            NetclawProtoMapper.ToProto(current).ToByteArray());
        var wireCandidate = Assert.Single(wire.Candidates);

        Assert.True(wireCandidate.HasAssignmentConstraintKind);
        Assert.True(wireCandidate.HasAssignmentDigest);

        // A reader from before assignment constraints sees only fields 1 through 4.
        var legacyProjection = new Netclaw.Security.ApprovalCandidate(
            wireCandidate.Verb,
            wireCandidate.HasDirectory ? wireCandidate.Directory : null)
        {
            Shell = wireCandidate.HasShell
                ? (Netclaw.Configuration.ApprovalShell)wireCandidate.Shell
                : null,
            VerbTokens = wireCandidate.VerbTokens.ToArray(),
        };

        Assert.Null(legacyProjection.AssignmentDigest);
        Assert.All(
            wire.OptionKeys.Where(static key => key.StartsWith("approve_assignment_", StringComparison.Ordinal)),
            key => Assert.Equal(ConsentAnswer.Denied, MapLegacyApprovalDecision(key)));
    }

    [Fact]
    public void ToolApprovalRequested_maps_a_legacy_candidate_to_explicit_none()
    {
        var proto = new Serialization.Proto.ToolApprovalRequestedProto
        {
            SessionId = new Serialization.Proto.SessionIdProto { Value = "session-1" },
            CallId = "call-legacy-candidate",
            ToolName = "shell_execute",
            RequestedAtMs = 1700000000000,
        };
        proto.Candidates.Add(new Serialization.Proto.ToolApprovalRequestedProto.Types.ApprovalCandidateProto
        {
            Verb = "git",
        });

        var result = NetclawProtoMapper.FromProto(proto);

        var candidate = Assert.Single(result.Candidates);
        Assert.Null(candidate.AssignmentDigest);
    }

    [Fact]
    public void ToolApprovalRequested_rejects_a_digest_without_a_constraint_kind()
    {
        var proto = new Serialization.Proto.ToolApprovalRequestedProto
        {
            SessionId = new Serialization.Proto.SessionIdProto { Value = "session-1" },
            CallId = "call-invalid-candidate",
            ToolName = "shell_execute",
            RequestedAtMs = 1700000000000,
        };
        proto.Candidates.Add(new Serialization.Proto.ToolApprovalRequestedProto.Types.ApprovalCandidateProto
        {
            Verb = "git",
            AssignmentDigest = $"sha256:{new string('a', 64)}",
        });

        Assert.Throws<InvalidOperationException>(() => NetclawProtoMapper.FromProto(proto));
    }

    [Fact]
    public void ToolApprovalResolved_round_trips_authorization_attempt_id()
    {
        var wrapped = new ToolApprovalResolved
        {
            SessionId = new SessionId("C123/1700000000.000001"),
            CallId = "call-resolved-1",
            AuthorizationAttemptId = "auth-fedcba9876543210fedcba9876543210",
            Decision = "ApprovedOnce",
            ResolvedAtMs = 1700000001000
        };

        var result = RoundTrip(wrapped);

        Assert.Equal(wrapped.SessionId, result.SessionId);
        Assert.Equal(wrapped.CallId, result.CallId);
        Assert.Equal(wrapped.AuthorizationAttemptId, result.AuthorizationAttemptId);
        Assert.Equal(wrapped.Decision, result.Decision);
        Assert.Equal(wrapped.ResolvedAtMs, result.ResolvedAtMs);
    }

    [Fact]
    public void ToolApprovalRequested_writes_only_managed_temporary_directory()
    {
        var wrapped = new ToolApprovalRequested
        {
            SessionId = new SessionId("C123/1700000000.000001"),
            CallId = "call-managed-temp",
            ToolName = "file_write",
            Audience = Netclaw.Configuration.TrustAudience.Personal,
            SessionScratchDirectory = "/legacy/session-directory",
            ManagedTemporaryDirectory = "/managed/tmp/parent",
            RequestedAtMs = 1700000000000
        };

        var proto = NetclawProtoMapper.ToProto(wrapped);

        Assert.False(proto.HasSessionScratchDirectory);
        Assert.True(proto.HasManagedTemporaryDirectory);
        Assert.Equal(wrapped.ManagedTemporaryDirectory, proto.ManagedTemporaryDirectory);
    }

    [Fact]
    public void ToolApprovalRequested_reads_legacy_session_directory_without_temp_reinterpretation()
    {
        var proto = new Serialization.Proto.ToolApprovalRequestedProto
        {
            SessionId = new Serialization.Proto.SessionIdProto
            {
                Value = "C123/1700000000.000001"
            },
            CallId = "call-legacy-temp",
            ToolName = "file_write",
            Audience = Serialization.Proto.TrustAudience.Personal,
            SessionScratchDirectory = "/legacy/session-directory",
            RequestedAtMs = 1700000000000
        };

        var result = NetclawProtoMapper.FromProto(proto);

        Assert.Equal(proto.SessionScratchDirectory, result.SessionScratchDirectory);
        Assert.Null(result.ManagedTemporaryDirectory);
    }

    [Fact]
    public void ToolApprovalRequested_legacy_event_round_trips_without_turn_context()
    {
        var wrapped = new ToolApprovalRequested
        {
            SessionId = new SessionId("C123/1700000000.000001"),
            CallId = "call-legacy-approval",
            ToolName = "shell_execute",
            Patterns = ["git status"],
            CandidateVerbs = ["git"],
            Audience = Netclaw.Configuration.TrustAudience.Team,
            Boundary = Netclaw.Configuration.TrustBoundary.Team,
            ChannelType = "slack",
            SupportsInteractiveApproval = true,
            RequesterSenderId = new SenderId("U12345"),
            RequesterPrincipal = Netclaw.Configuration.PrincipalClassification.TrustedInternal,
            OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
            RequestedAtMs = 1700000000000
        };

        var result = RoundTrip(wrapped);

        Assert.Equal(wrapped.SessionId, result.SessionId);
        Assert.Equal(wrapped.CallId, result.CallId);
        Assert.Equal(wrapped.ToolName, result.ToolName);
        Assert.Equal(wrapped.Patterns, result.Patterns);
        Assert.Equal(wrapped.CandidateVerbs, result.CandidateVerbs);
        Assert.Equal(wrapped.Audience, result.Audience);
        Assert.Equal(wrapped.Boundary, result.Boundary);
        Assert.Equal(wrapped.ChannelType, result.ChannelType);
        Assert.Equal(wrapped.SupportsInteractiveApproval, result.SupportsInteractiveApproval);
        Assert.Equal(wrapped.RequesterSenderId, result.RequesterSenderId);
        Assert.Equal(wrapped.RequesterPrincipal, result.RequesterPrincipal);
        Assert.Null(result.AuthorizationAttemptId);
        Assert.Null(result.RepositoryCommonDirectory);
        Assert.Equal(wrapped.OptionKeys, result.OptionKeys);
        Assert.Null(result.TurnContext);
        Assert.Equal(wrapped.RequestedAtMs, result.RequestedAtMs);
    }

    [Fact]
    public void SessionSnapshot_has_no_pending_approval_projection()
    {
        var wrapped = new SessionSnapshot
        {
            TurnCount = 2,
            History = []
        };

        var expected = new Serialization.Proto.SessionSnapshotProto
        {
            TurnCount = 2
        }.ToByteArray();

        Assert.Equal(expected, SerializeLegacySnapshot(wrapped));
        var codec = new Serialization.NetclawProtobufSerializer((Akka.Actor.ExtendedActorSystem)Sys);
        Assert.Equal("ss-v2", codec.Manifest(wrapped));
        Assert.Equal(2, RoundTrip(wrapped).TurnCount);
    }

    [Fact]
    public void AdoptedContextRecorded_sender_id_wrap_is_byte_identical_to_raw_primitive_proto()
    {
        // Pass 7c wraps the adopted-context SenderId / AuthorizerSenderId fields
        // in the SenderId value object. The journal bytes must stay identical to
        // the pre-wrap raw-string representation so a daemon running the new
        // binary can replay events written by the old one.
        var wrapped = new AdoptedContextRecorded
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            AuthorizedMessageId = "msg-42",
            AuthorizerSenderId = new SenderId("U12345"),
            Projection = "Alice said hello",
            HasAdoptedContext = true,
            ProjectionPersisted = true,
            RecordedAtMs = 1708531200000,
            Messages =
            [
                new AdoptedContextRecorded.AdoptedMessageRecord
                {
                    MessageId = "msg-41",
                    SenderId = new SenderId("U67890"),
                    TimestampMs = 1708531190000,
                    AuthorityAtInclusion = "verified"
                }
            ]
        };

        var expected = new Serialization.Proto.AdoptedContextRecordedProto
        {
            SessionId = new Serialization.Proto.SessionIdProto { Value = "C99999/1708531200.000100" },
            AuthorizedMessageId = "msg-42",
            AuthorizerSenderId = "U12345",
            Projection = "Alice said hello",
            HasAdoptedContext = true,
            ProjectionPersisted = true,
            RecordedAtMs = 1708531200000,
            Messages =
            {
                new Serialization.Proto.AdoptedContextRecordedProto.Types.AdoptedMessageRecordProto
                {
                    MessageId = "msg-41",
                    SenderId = "U67890",
                    TimestampMs = 1708531190000,
                    AuthorityAtInclusion = "verified"
                }
            }
        }.ToByteArray();

        Assert.Equal(expected, Serialize(wrapped));

        var result = RoundTrip(wrapped);
        Assert.Equal(new SenderId("U12345"), result.AuthorizerSenderId);
        Assert.Equal(new SenderId("U67890"), Assert.Single(result.Messages).SenderId);
    }

    [Fact]
    public void MemoriesDistilledV2_with_empty_collections_round_trips()
    {
        // Edge: distillation with zero anchors / zero proposals is a real
        // outcome when the LLM declines to propose anything. The wire
        // shape must survive empty-list serialization without throwing.
        var original = new MemoriesDistilledV2(
            Anchors: [],
            Proposals: [],
            TimestampMs: 0);

        var result = RoundTrip(original);

        Assert.Empty(result.Anchors);
        Assert.Empty(result.Proposals);
        Assert.Equal(0L, result.TimestampMs);
    }

    // --- The reminder link of a child run: BackgroundChildRunV2Proto field 20 ---
    // A child-delivery turn copies the link of its child run to TurnRecorded.SourceReminderId. The link must survive
    // the acceptance event (journal replay) and the snapshot child list (snapshot restore). No open branch edits
    // BackgroundChildRunV2Proto, and dev uses fields 1 to 19.

    private const int ChildRunSourceReminderField = 20;
    private static readonly ReminderId ChildRunFireKey = new("check-pr:1712000000000");

    private static BackgroundChildRun ChildRunWithReminderLink(ReminderId? link) => new()
    {
        RunId = new SubAgentRunId("run-link-1"),
        ScopeId = new SubAgentScopeId("scope-link-1"),
        StartKey = new ChildRunStartKey.Tool(new SessionId("session-link-1"), "turn-link-1", new ToolCallId("call-link-1")),
        AgentName = new AgentName("worker"),
        SourceOperation = "spawn_agent",
        ArgumentsDigest = "digest-link-1",
        OriginalContext = new TurnContextRecord
        {
            SessionId = new SessionId("session-link-1"),
            TurnId = "turn-link-1",
            Boundary = TrustBoundary.Team,
            RequesterPrincipal = PrincipalClassification.VerifiedAutomation
        },
        InitialWorkingSnapshot = new WorkingContextSnapshot
        {
            WorkingContext = WorkingContext.Empty,
            Git = new GitWorkingContextInspection.Skipped()
        },
        AcceptedAtMs = 1,
        Lifecycle = new ChildRunLifecycle.Accepted(null),
        SourceReminderId = link
    };

    private static ChildRunAccepted AcceptanceOf(BackgroundChildRun run) => new()
    {
        SessionId = new SessionId("session-link-1"),
        Run = run,
        RecordedAtMs = 1
    };

    // The run record inside the acceptance event, as the journal writes it.
    private byte[] AcceptedRunRecord(ChildRunAccepted accepted)
        => Serialization.Proto.ChildRunAcceptedV2Proto.Parser.ParseFrom(Serialize(accepted)).Run.ToByteArray();

    [Fact]
    public void The_child_run_reminder_link_uses_field_20_with_explicit_presence()
    {
        var field = Serialization.Proto.BackgroundChildRunV2Proto.Descriptor.FindFieldByName("source_reminder_id");

        Assert.Equal(ChildRunSourceReminderField, field.FieldNumber);
        // Presence tells "no reminder" from an empty key.
        Assert.True(field.HasPresence);
    }

    [Fact]
    public void ChildRunAccepted_keeps_the_reminder_link_of_the_turn_that_started_the_child()
    {
        var accepted = AcceptanceOf(ChildRunWithReminderLink(ChildRunFireKey));

        var result = RoundTrip(accepted);

        Assert.Equal(ChildRunFireKey, result.Run.SourceReminderId);
        Assert.Contains(ChildRunSourceReminderField, WireFieldNumbers(AcceptedRunRecord(accepted)));
    }

    [Fact]
    public void ChildRunAccepted_of_a_person_turn_writes_no_reminder_link()
    {
        var accepted = AcceptanceOf(ChildRunWithReminderLink(null));

        var result = RoundTrip(accepted);

        Assert.Null(result.Run.SourceReminderId);
        Assert.DoesNotContain(ChildRunSourceReminderField, WireFieldNumbers(AcceptedRunRecord(accepted)));
    }

    [Fact]
    public void A_child_run_record_from_before_the_link_reads_as_no_link_and_a_hand_built_field_20_reads_as_the_link()
    {
        // A record without field 20 is what a journal from before this change holds. It reads as no reminder, so
        // the child-delivery turn of such a child raises no alert.
        var oldShape = AcceptanceOf(ChildRunWithReminderLink(null));
        var oldBytes = Serialize(oldShape);
        Assert.Null(Deserialize(oldBytes, oldShape).Run.SourceReminderId);

        var proto = Serialization.Proto.ChildRunAcceptedV2Proto.Parser.ParseFrom(oldBytes);
        proto.Run = Serialization.Proto.BackgroundChildRunV2Proto.Parser.ParseFrom(
            WithStringField(proto.Run.ToByteArray(), ChildRunSourceReminderField, ChildRunFireKey.Value));

        Assert.Equal(ChildRunFireKey, Deserialize(proto.ToByteArray(), oldShape).Run.SourceReminderId);
    }

    [Fact]
    public void SessionSnapshot_child_run_keeps_the_reminder_link()
    {
        // A session that restores from a snapshot reads the child run from the snapshot, not from the
        // acceptance event. The child-delivery turn after the restore needs the link from here.
        var snapshot = new SessionSnapshot
        {
            TurnCount = 1,
            History = [],
            ChildRuns = [ChildRunWithReminderLink(ChildRunFireKey)]
        };

        var restored = RoundTrip(snapshot);

        Assert.Equal(ChildRunFireKey, Assert.Single(restored.ChildRuns).SourceReminderId);
    }

    private byte[] SerializeLegacySnapshot(SessionSnapshot snapshot)
        => NetclawProtoMapper.ToProto(snapshot).ToByteArray();

    private SessionSnapshot DeserializeLegacySnapshot(byte[] bytes)
        => Assert.IsType<SessionSnapshot>(new NetclawProtobufSerializer(
            (Akka.Actor.ExtendedActorSystem)Sys).FromBinary(bytes, "ss-v1"));

    private static ConsentAnswer MapLegacyApprovalDecision(string selectedKey) => selectedKey switch
    {
        ApprovalOptionKeys.ApproveOnce => ConsentAnswer.Once.Instance,
        ApprovalOptionKeys.ApproveSession => new ConsentAnswer.Grant(GrantScopeKind.Session),
        ApprovalOptionKeys.ApproveAlways => new ConsentAnswer.Grant(GrantScopeKind.Folder),
        ApprovalOptionKeys.ApproveRepository => new ConsentAnswer.Grant(GrantScopeKind.Repository),
        ApprovalOptionKeys.ApproveEverywhere => new ConsentAnswer.Grant(GrantScopeKind.Everywhere),
        ApprovalOptionKeys.Deny => ConsentAnswer.Denied,
        _ => ConsentAnswer.Denied,
    };
}
