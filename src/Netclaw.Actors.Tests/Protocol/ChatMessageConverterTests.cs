// -----------------------------------------------------------------------
// <copyright file="ChatMessageConverterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using ChatRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// Tests boundary conversion between persistence-safe <see cref="SerializableChatMessage"/>
/// and MEAI <see cref="AiChatMessage"/> types.
/// </summary>
public class ChatMessageConverterTests
{
    public static TheoryData<ChatRole, AiChatRole> RoleMappings => new()
    {
        { ChatRole.User, AiChatRole.User },
        { ChatRole.Assistant, AiChatRole.Assistant },
        { ChatRole.System, AiChatRole.System },
        { ChatRole.Tool, AiChatRole.Tool },
    };

    [Theory]
    [MemberData(nameof(RoleMappings))]
    public void ToAiMessage_maps_role_correctly(ChatRole inputRole, AiChatRole expectedAiRole)
    {
        var msg = new SerializableChatMessage { Role = inputRole, Content = "test" };
        var ai = ChatMessageConverter.ToAiMessage(msg);
        Assert.Equal(expectedAiRole, ai.Role);
        Assert.Equal("test", ai.Text);
    }

    [Theory]
    [MemberData(nameof(RoleMappings))]
    public void FromAiMessage_maps_role_correctly(ChatRole expectedRole, AiChatRole aiRole)
    {
        var ai = new AiChatMessage(aiRole, "test");
        var msg = ChatMessageConverter.FromAiMessage(ai);
        Assert.Equal(expectedRole, msg.Role);
        Assert.Equal("test", msg.Content);
    }

    [Fact]
    public void ToAiMessage_preserves_content()
    {
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = "Hello, how are you?"
        };

        var ai = ChatMessageConverter.ToAiMessage(msg);

        Assert.Equal("Hello, how are you?", ai.Text);
    }

    [Fact]
    public void FromAiMessage_handles_null_text()
    {
        // ChatMessage with no text content
        var ai = new AiChatMessage(AiChatRole.Assistant, (string?)null);

        var msg = ChatMessageConverter.FromAiMessage(ai);

        Assert.Equal(ChatRole.Assistant, msg.Role);
        Assert.Equal(string.Empty, msg.Content);
    }

    [Fact]
    public void ToAiMessages_converts_full_conversation()
    {
        var messages = new List<SerializableChatMessage>
        {
            new() { Role = ChatRole.System, Content = "You are helpful." },
            new() { Role = ChatRole.User, Content = "Hello" },
            new() { Role = ChatRole.Assistant, Content = "Hi there!" },
            new() { Role = ChatRole.User, Content = "What time is it?" },
        };

        var aiMessages = ChatMessageConverter.ToAiMessages(messages);

        Assert.Equal(4, aiMessages.Count);
        Assert.Equal(AiChatRole.System, aiMessages[0].Role);
        Assert.Equal(AiChatRole.User, aiMessages[1].Role);
        Assert.Equal(AiChatRole.Assistant, aiMessages[2].Role);
        Assert.Equal(AiChatRole.User, aiMessages[3].Role);

        Assert.Equal("You are helpful.", aiMessages[0].Text);
        Assert.Equal("Hello", aiMessages[1].Text);
        Assert.Equal("Hi there!", aiMessages[2].Text);
        Assert.Equal("What time is it?", aiMessages[3].Text);
    }

    [Fact]
    public void Round_trip_preserves_role_and_content()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "Here is my response."
        };

        var ai = ChatMessageConverter.ToAiMessage(original);
        var roundTripped = ChatMessageConverter.FromAiMessage(ai);

        Assert.Equal(original.Role, roundTripped.Role);
        Assert.Equal(original.Content, roundTripped.Content);
    }

    [Fact]
    public void ToAiMessages_empty_list_returns_empty()
    {
        var result = ChatMessageConverter.ToAiMessages(Array.Empty<SerializableChatMessage>());
        Assert.Empty(result);
    }

    // ── Tool call / result round-trip tests ──

    [Fact]
    public void FromAiMessage_captures_tool_calls_from_assistant()
    {
        var contents = new List<AIContent>
        {
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" }),
            new FunctionCallContent("call-2", "fetch",
                new Dictionary<string, object?> { ["url"] = "https://example.com" })
        };
        var ai = new AiChatMessage(AiChatRole.Assistant, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai);

        Assert.Equal(ChatRole.Assistant, msg.Role);
        Assert.Equal(2, msg.ToolCalls.Count);
        Assert.Equal("call-1", msg.ToolCalls[0].CallId.Value);
        Assert.Equal("web_search", msg.ToolCalls[0].Name.Value);
        Assert.Equal("call-2", msg.ToolCalls[1].CallId.Value);
        Assert.Equal("fetch", msg.ToolCalls[1].Name.Value);
    }

    [Fact]
    public void ToAiMessage_reconstructs_tool_calls()
    {
        var msg = new SerializableChatMessage
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

        var ai = ChatMessageConverter.ToAiMessage(msg);

        Assert.Equal(AiChatRole.Assistant, ai.Role);
        var toolCall = Assert.Single(ai.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-1", toolCall.CallId);
        Assert.Equal("web_search", toolCall.Name);
    }

    [Fact]
    public void Tool_result_message_round_trips()
    {
        var original = new SerializableChatMessage
        {
            Role = ChatRole.Tool,
            Content = "Found 3 results",
            ToolCallId = new Netclaw.Tools.ToolCallId("call-1"),
            Name = "web_search"
        };

        var ai = ChatMessageConverter.ToAiMessage(original);
        Assert.Equal(AiChatRole.Tool, ai.Role);

        var resultContent = Assert.Single(ai.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-1", resultContent.CallId);
        Assert.Equal("Found 3 results", resultContent.Result?.ToString());

        var roundTripped = ChatMessageConverter.FromAiMessage(ai);
        Assert.Equal(ChatRole.Tool, roundTripped.Role);
        Assert.Equal("call-1", roundTripped.ToolCallId?.Value);
        Assert.Equal("Found 3 results", roundTripped.Content);
    }

    [Fact]
    public void Assistant_message_with_text_and_tool_calls_preserves_both()
    {
        var contents = new List<AIContent>
        {
            new TextContent("Let me search for that."),
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        };
        var ai = new AiChatMessage(AiChatRole.Assistant, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai);

        Assert.Equal("Let me search for that.", msg.Content);
        Assert.Single(msg.ToolCalls);
        Assert.Equal("web_search", msg.ToolCalls[0].Name.Value);
    }

    // ── Media / DataContent round-trip tests ──

    [Fact]
    public void FromAiMessage_writes_DataContent_to_session_dir_and_produces_media_reference()
    {
        using var tempDir = new TempSessionDir();
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var contents = new List<AIContent>
        {
            new TextContent("Check this image"),
            new DataContent(imageBytes, "image/png")
        };
        var ai = new AiChatMessage(AiChatRole.User, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai, sessionDir: tempDir.Path);

        Assert.Equal("Check this image", msg.Content);
        Assert.Single(msg.MediaReferences);
        Assert.Equal("image/png", msg.MediaReferences[0].MimeType.Value);
        Assert.Equal((int)MediaModality.Image, msg.MediaReferences[0].Modality);
        Assert.EndsWith(".png", msg.MediaReferences[0].RelativePath);
        // FileSizeBytes is populated at write time so compaction's token
        // estimator can account for base64-encoded media payload size without
        // touching disk.
        Assert.Equal(imageBytes.Length, msg.MediaReferences[0].FileSizeBytes);

        // Verify file was written to disk
        var filePath = Path.Combine(tempDir.Path, "media", msg.MediaReferences[0].RelativePath);
        Assert.True(File.Exists(filePath));
        Assert.Equal(imageBytes, File.ReadAllBytes(filePath));
    }

    [Fact]
    public void ToAiMessage_reads_media_files_and_produces_DataContent()
    {
        using var tempDir = new TempSessionDir();
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header
        var mediaDir = Path.Combine(tempDir.Path, "media");
        Directory.CreateDirectory(mediaDir);
        File.WriteAllBytes(Path.Combine(mediaDir, "test.jpg"), imageBytes);

        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = "Look at this",
            MediaReferences =
            [
                new SerializableMediaReference
                {
                    RelativePath = "test.jpg",
                    MimeType = new Netclaw.Security.MimeType("image/jpeg"),
                    Modality = (int)MediaModality.Image
                }
            ]
        };

        var ai = ChatMessageConverter.ToAiMessage(msg, sessionDir: tempDir.Path);

        Assert.Equal(AiChatRole.User, ai.Role);
        var textContent = Assert.Single(ai.Contents.OfType<TextContent>());
        Assert.Equal("Look at this", textContent.Text);
        var dataContent = Assert.Single(ai.Contents.OfType<DataContent>());
        Assert.Equal("image/jpeg", dataContent.MediaType);
        Assert.Equal(imageBytes, dataContent.Data.ToArray());
    }

    [Fact]
    public void Full_media_round_trip_through_converter()
    {
        using var tempDir = new TempSessionDir();
        var imageBytes = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }; // GIF89a header

        // Step 1: AI message with DataContent → SerializableChatMessage
        var originalAi = new AiChatMessage(AiChatRole.User,
        [
            new TextContent("Here is a gif"),
            new DataContent(imageBytes, "image/gif")
        ]);
        var serializable = ChatMessageConverter.FromAiMessage(originalAi, sessionDir: tempDir.Path);
        Assert.Single(serializable.MediaReferences);

        // Step 2: SerializableChatMessage → AI message (reads file back)
        var reconstructed = ChatMessageConverter.ToAiMessage(serializable, sessionDir: tempDir.Path);

        var text = Assert.Single(reconstructed.Contents.OfType<TextContent>());
        Assert.Equal("Here is a gif", text.Text);
        var data = Assert.Single(reconstructed.Contents.OfType<DataContent>());
        Assert.Equal("image/gif", data.MediaType);
        Assert.Equal(imageBytes, data.Data.ToArray());
    }

    [Fact]
    public void ToAiMessage_skips_missing_media_files_gracefully()
    {
        using var tempDir = new TempSessionDir();

        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = "Image was deleted",
            MediaReferences =
            [
                new SerializableMediaReference
                {
                    RelativePath = "nonexistent.png",
                    MimeType = new Netclaw.Security.MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                }
            ]
        };

        var ai = ChatMessageConverter.ToAiMessage(msg, sessionDir: tempDir.Path);

        // Text content should still be present, but no DataContent
        Assert.Equal("Image was deleted", ai.Text);
        Assert.Empty(ai.Contents.OfType<DataContent>());
    }

    [Fact]
    public void FromAiMessage_ignores_empty_DataContent()
    {
        using var tempDir = new TempSessionDir();
        var contents = new List<AIContent>
        {
            new TextContent("No actual data"),
            new DataContent(Array.Empty<byte>(), "image/png")
        };
        var ai = new AiChatMessage(AiChatRole.User, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai, sessionDir: tempDir.Path);

        Assert.Equal("No actual data", msg.Content);
        Assert.Empty(msg.MediaReferences);
    }

    [Fact]
    public void MimeToExtension_maps_common_types()
    {
        Assert.Equal(".png", ChatMessageConverter.MimeToExtension("image/png"));
        Assert.Equal(".jpg", ChatMessageConverter.MimeToExtension("image/jpeg"));
        Assert.Equal(".gif", ChatMessageConverter.MimeToExtension("image/gif"));
        Assert.Equal(".webp", ChatMessageConverter.MimeToExtension("image/webp"));
        Assert.Equal(".mp3", ChatMessageConverter.MimeToExtension("audio/mpeg"));
        Assert.Equal(".bin", ChatMessageConverter.MimeToExtension("application/octet-stream"));
    }

    [Fact]
    public void MimeToModality_classifies_correctly()
    {
        Assert.Equal(MediaModality.Image, ChatMessageConverter.MimeToModality("image/png"));
        Assert.Equal(MediaModality.Image, ChatMessageConverter.MimeToModality("image/jpeg"));
        Assert.Equal(MediaModality.Audio, ChatMessageConverter.MimeToModality("audio/mpeg"));
        Assert.Equal(MediaModality.Video, ChatMessageConverter.MimeToModality("video/mp4"));
    }

    // ── Tool call meta extraction tests ──

    [Fact]
    public void FromAiMessage_extracts_meta_from_tool_call_arguments()
    {
        var args = new Dictionary<string, object?>
        {
            ["Command"] = "dotnet test",
            ["_rationale"] = "running tests to verify refactor",
            ["_timeout_seconds"] = 300,
            ["_background"] = false
        };
        var contents = new List<AIContent>
        {
            new FunctionCallContent("call-1", "shell_execute", args)
        };
        var ai = new AiChatMessage(AiChatRole.Assistant, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai);

        var tc = Assert.Single(msg.ToolCalls);
        Assert.NotNull(tc.MetaJson);

        var meta = ToolCallMeta.Parse(tc.MetaJson);
        Assert.NotNull(meta);
        Assert.Equal("running tests to verify refactor", meta.Rationale);
        Assert.Equal(300, meta.TimeoutHintSeconds);
        Assert.False(meta.Background);

        // Arguments should be clean (no meta fields)
        Assert.Contains("Command", tc.ArgumentsJson);
        Assert.DoesNotContain("_rationale", tc.ArgumentsJson);
        Assert.DoesNotContain("_timeout_seconds", tc.ArgumentsJson);
        Assert.DoesNotContain("_background", tc.ArgumentsJson);
    }

    [Fact]
    public void FromAiMessage_tool_call_without_meta_has_null_MetaJson()
    {
        var args = new Dictionary<string, object?>
        {
            ["Command"] = "ls -la"
        };
        var contents = new List<AIContent>
        {
            new FunctionCallContent("call-1", "shell_execute", args)
        };
        var ai = new AiChatMessage(AiChatRole.Assistant, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai);

        var tc = Assert.Single(msg.ToolCalls);
        Assert.Null(tc.MetaJson);
        Assert.Contains("Command", tc.ArgumentsJson);
    }

    [Fact]
    public void FromAiMessage_tool_call_with_only_rationale_produces_meta()
    {
        var args = new Dictionary<string, object?>
        {
            ["query"] = "Akka.NET persistence",
            ["_rationale"] = "searching for docs"
        };
        var contents = new List<AIContent>
        {
            new FunctionCallContent("call-1", "web_search", args)
        };
        var ai = new AiChatMessage(AiChatRole.Assistant, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai);

        var tc = Assert.Single(msg.ToolCalls);
        Assert.NotNull(tc.MetaJson);

        var meta = ToolCallMeta.Parse(tc.MetaJson);
        Assert.NotNull(meta);
        Assert.Equal("searching for docs", meta.Rationale);
        Assert.Null(meta.TimeoutHintSeconds);
        Assert.False(meta.Background);
    }

    [Fact]
    public void FromAiMessage_tool_call_with_background_true()
    {
        var args = new Dictionary<string, object?>
        {
            ["Command"] = "dotnet build",
            ["_rationale"] = "building project",
            ["_background"] = true
        };
        var contents = new List<AIContent>
        {
            new FunctionCallContent("call-1", "shell_execute", args)
        };
        var ai = new AiChatMessage(AiChatRole.Assistant, contents);

        var msg = ChatMessageConverter.FromAiMessage(ai);
        var meta = ToolCallMeta.Parse(msg.ToolCalls[0].MetaJson);

        Assert.NotNull(meta);
        Assert.True(meta.Background);
    }

    [Fact]
    public void ExtractMeta_handles_null_arguments()
    {
        var (meta, clean) = ChatMessageConverter.ExtractMeta(null);
        Assert.Null(meta);
        Assert.Null(clean);
    }

    [Fact]
    public void ExtractMeta_handles_empty_arguments()
    {
        var args = new Dictionary<string, object?>();
        var (meta, clean) = ChatMessageConverter.ExtractMeta(args);
        Assert.Null(meta);
        Assert.Same(args, clean);
    }

    /// <summary>Disposable temp directory for session media tests.</summary>
    private sealed class TempSessionDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");

        public TempSessionDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
