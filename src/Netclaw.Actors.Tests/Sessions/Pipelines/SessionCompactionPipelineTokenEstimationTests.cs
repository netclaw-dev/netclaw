// -----------------------------------------------------------------------
// <copyright file="SessionCompactionPipelineTokenEstimationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Pipelines;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

/// <summary>
/// Unit tests for <see cref="SessionCompactionPipeline.EstimateTokens"/> with
/// emphasis on media-reference accounting. The naive char/4 estimator must
/// include base64-inflated media payload size so the adaptive compaction loop
/// can react to image-heavy turns. Legacy records (FileSizeBytes = 0) must
/// produce the same estimate as before this field existed.
/// </summary>
public class SessionCompactionPipelineTokenEstimationTests
{
    [Fact]
    public void EstimateTokens_text_only_message_matches_char_quartile()
    {
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = new string('a', 400)
        };

        var tokens = SessionCompactionPipeline.EstimateTokens([msg], systemPrompt: null);

        Assert.Equal(100, tokens);
    }

    [Fact]
    public void EstimateTokens_legacy_media_reference_without_FileSizeBytes_does_not_inflate()
    {
        // Legacy records persisted before the FileSizeBytes field existed
        // deserialize with FileSizeBytes = 0 (proto3 default). The estimator
        // must under-count them the same way it did before — no regression.
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = new string('a', 400),
            MediaReferences =
            [
                new SerializableMediaReference
                {
                    RelativePath = "img.png",
                    MimeType = new Netclaw.Security.MimeType("image/png"),
                    Modality = (int)MediaModality.Image
                    // FileSizeBytes intentionally omitted → 0
                }
            ]
        };

        var tokens = SessionCompactionPipeline.EstimateTokens([msg], systemPrompt: null);

        Assert.Equal(100, tokens);
    }

    [Fact]
    public void EstimateTokens_includes_media_size_inflated_by_base64_ratio()
    {
        // A 300KB image base64-encodes to ~400KB chars (4/3 inflation).
        // At chars/4, that contributes 100,000 tokens above the text content.
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = string.Empty,
            MediaReferences =
            [
                new SerializableMediaReference
                {
                    RelativePath = "img.png",
                    MimeType = new Netclaw.Security.MimeType("image/png"),
                    Modality = (int)MediaModality.Image,
                    FileSizeBytes = 300_000
                }
            ]
        };

        var tokens = SessionCompactionPipeline.EstimateTokens([msg], systemPrompt: null);

        // 300000 * 4 / 3 / 4 == 100000
        Assert.Equal(100_000, tokens);
    }

    [Fact]
    public void EstimateTokens_sums_multiple_media_references_on_one_message()
    {
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = string.Empty,
            MediaReferences =
            [
                new SerializableMediaReference { RelativePath = "a.png", MimeType = new Netclaw.Security.MimeType("image/png"), FileSizeBytes = 60_000 },
                new SerializableMediaReference { RelativePath = "b.png", MimeType = new Netclaw.Security.MimeType("image/png"), FileSizeBytes = 30_000 },
                new SerializableMediaReference { RelativePath = "c.png", MimeType = new Netclaw.Security.MimeType("image/png"), FileSizeBytes = 30_000 }
            ]
        };

        var tokens = SessionCompactionPipeline.EstimateTokens([msg], systemPrompt: null);

        // (60_000 + 30_000 + 30_000) * 4 / 3 / 4 == 40_000
        Assert.Equal(40_000, tokens);
    }

    [Fact]
    public void EstimateTokens_combines_text_tool_call_args_and_media()
    {
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            Content = new string('a', 400), // contributes 100 tokens
            ToolCalls =
            [
                new SerializableToolCall
                {
                    CallId = new Netclaw.Tools.ToolCallId("call-1"),
                    Name = new Netclaw.Tools.ToolName("shell_execute"),
                    ArgumentsJson = new string('b', 200) // contributes 50 tokens
                }
            ],
            MediaReferences =
            [
                new SerializableMediaReference { RelativePath = "img.png", MimeType = new Netclaw.Security.MimeType("image/png"), FileSizeBytes = 300_000 } // contributes 100_000 tokens
            ]
        };

        var tokens = SessionCompactionPipeline.EstimateTokens([msg], systemPrompt: null);

        Assert.Equal(100 + 50 + 100_000, tokens);
    }

    [Fact]
    public void EstimateTokens_includes_system_prompt_media()
    {
        var systemPrompt = new SerializableChatMessage
        {
            Role = ChatRole.System,
            Content = new string('s', 800), // contributes 200 tokens
            MediaReferences =
            [
                new SerializableMediaReference { RelativePath = "logo.png", MimeType = new Netclaw.Security.MimeType("image/png"), FileSizeBytes = 150_000 } // contributes 50_000
            ]
        };

        var tokens = SessionCompactionPipeline.EstimateTokens([], systemPrompt);

        Assert.Equal(200 + 50_000, tokens);
    }

    [Fact]
    public void EstimateTokens_long_accumulator_does_not_overflow_on_multimegabyte_media()
    {
        // A 4 MB image base64-encodes to ~5.33 MB chars. With an int accumulator
        // we'd risk overflow when summing across many messages. The long
        // accumulator must absorb this and produce a sane token count.
        var bigImage = new SerializableMediaReference
        {
            RelativePath = "huge.png",
            MimeType = new Netclaw.Security.MimeType("image/png"),
            FileSizeBytes = 4_000_000
        };
        var messages = new List<SerializableChatMessage>();
        for (var i = 0; i < 5; i++)
        {
            messages.Add(new SerializableChatMessage
            {
                Role = ChatRole.User,
                Content = string.Empty,
                MediaReferences = [bigImage]
            });
        }

        var tokens = SessionCompactionPipeline.EstimateTokens(messages, systemPrompt: null);

        // 5 * (4_000_000 * 4 / 3 / 4) == 5 * ~1_333_333 == ~6_666_665
        Assert.InRange(tokens, 6_000_000, 7_000_000);
    }
}
