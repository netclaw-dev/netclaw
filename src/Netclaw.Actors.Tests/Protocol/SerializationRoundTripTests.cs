// -----------------------------------------------------------------------
// <copyright file="SerializationRoundTripTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Serialization;
using Google.Protobuf;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Xunit;

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
    public void TurnBroadcast_round_trips()
    {
        var ts = new DateTimeOffset(2026, 2, 21, 10, 1, 5, TimeSpan.Zero);
        var original = new TurnBroadcast
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            AssistantReply = new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "Here is your answer."
            },
            BroadcastAtMs = ts.ToUnixTimeMilliseconds()
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(ChatRole.Assistant, result.AssistantReply.Role);
        Assert.Equal("Here is your answer.", result.AssistantReply.Content);
        Assert.Equal(original.BroadcastAtMs, result.BroadcastAtMs);
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
                    MimeType = new Netclaw.Security.MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                },
                new SerializableMediaReference
                {
                    RelativePath = "def456.jpg",
                    MimeType = new Netclaw.Security.MimeType("image/jpeg"),
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
                    MimeType = new Netclaw.Security.MimeType("image/png"),
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
    public void CompactionBroadcast_round_trips()
    {
        var ts = new DateTimeOffset(2026, 2, 21, 11, 0, 1, TimeSpan.Zero);
        var original = new CompactionBroadcast
        {
            SessionId = new SessionId("C99999/1708531200.000100"),
            Summary = "Context compacted after 42 turns.",
            CompactedAtMs = ts.ToUnixTimeMilliseconds()
        };

        var result = RoundTrip(original);

        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(original.Summary, result.Summary);
        Assert.Equal(original.CompactedAtMs, result.CompactedAtMs);
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
            MimeType = new Netclaw.Security.MimeType("image/png"),
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
        Assert.Equal(new Netclaw.Security.MimeType("image/png"), result.MimeType);
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

        Assert.Equal(expected, Serialize(wrapped));

        var result = RoundTrip(wrapped);
        Assert.Equal(new Netclaw.Actors.Jobs.BackgroundJobId("job-55"), result.ActiveBackgroundJobs[0].JobId);
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
}
