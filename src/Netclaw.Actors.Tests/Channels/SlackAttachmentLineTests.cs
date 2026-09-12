// -----------------------------------------------------------------------
// <copyright file="SlackAttachmentLineTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Media;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Unit tests for <see cref="AttachmentIngressFormatting.EscapeQuoted"/> and
/// <see cref="AttachmentIngressFormatting.BuildAttachmentLine"/> covering
/// control-character sanitization and quote/backslash escaping.
/// </summary>
public sealed class SlackAttachmentLineTests
{
    [Theory]
    [InlineData("normal.pdf", "normal.pdf")]
    [InlineData("file\nname.pdf", "file name.pdf")]
    [InlineData("file\r\nname.pdf", "file  name.pdf")]
    [InlineData("file\tname.pdf", "file name.pdf")]
    [InlineData("inject\u0001\u0002control.pdf", "inject  control.pdf")]
    public void EscapeQuoted_normalizes_control_chars_to_spaces(string input, string expected)
    {
        var result = AttachmentIngressFormatting.EscapeQuoted(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EscapeQuoted_escapes_backslash_and_double_quote()
    {
        Assert.Equal("has \\\"quotes\\\"", AttachmentIngressFormatting.EscapeQuoted("has \"quotes\""));
        Assert.Equal("has \\\\backslash", AttachmentIngressFormatting.EscapeQuoted("has \\backslash"));
    }

    [Fact]
    public void EscapeQuoted_passes_through_normal_text_unchanged()
    {
        const string plain = "report-Q4_2025 final.pdf";
        Assert.Equal(plain, AttachmentIngressFormatting.EscapeQuoted(plain));
    }

    [Theory]
    [InlineData("evil\nfile\r\nname.pdf", "application/pdf", null)]
    [InlineData("ok.pdf", "application/pdf", "some\nnote with\r\nnewlines")]
    public void BuildAttachmentLine_with_hostile_metadata_produces_single_parseable_line(
        string name, string mimeType, string? note)
    {
        var line = AttachmentIngressFormatting.BuildAttachmentLine(
            name: name,
            mimeType: mimeType,
            size: 1234,
            relativePath: "inbox/test.pdf",
            inlined: false,
            note: note);

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        Assert.StartsWith("[attachment]", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAcceptedProjection_uses_final_collision_safe_live_inbox_path()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-attachment-line-{Guid.NewGuid():N}");
        var inboxDir = Path.Combine(sessionDir, SessionDirectoryHelper.InboxSubdirectory);
        Directory.CreateDirectory(inboxDir);

        try
        {
            await InboxWriter.SanitizeReserveAndWriteAsync(
                inboxDir, "image.png", new byte[] { 1 }, TestContext.Current.CancellationToken);
            var renamedPath = await InboxWriter.SanitizeReserveAndWriteAsync(
                inboxDir, "image.png", new byte[] { 2 }, TestContext.Current.CancellationToken);

            var projection = await AttachmentIngressFormatting.BuildAcceptedProjectionAsync(
                renamedPath,
                "image.png",
                "image/png",
                AttachmentCategory.Image,
                inlineImages: true,
                size: 1,
                TestContext.Current.CancellationToken);

            Assert.EndsWith("image_1.png", renamedPath, StringComparison.Ordinal);
            Assert.Contains("path=\"inbox/image_1.png\"", projection.Line, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(sessionDir, "inbox", "image_1.png")));
            Assert.NotNull(projection.InlineContent);
        }
        finally
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAcceptedProjection_uses_final_stable_historical_inbox_path()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-historical-line-{Guid.NewGuid():N}");
        var inboxDir = Path.Combine(sessionDir, SessionDirectoryHelper.InboxSubdirectory);
        Directory.CreateDirectory(inboxDir);
        var stagedPath = Path.Combine(sessionDir, "stage.tmp");
        await File.WriteAllBytesAsync(stagedPath, [1, 2, 3], TestContext.Current.CancellationToken);

        try
        {
            var historicalPath = HistoricalAttachmentInbox.PromoteOrReuse(
                inboxDir, "image.png", "slack:F123", stagedPath);
            var projection = await AttachmentIngressFormatting.BuildAcceptedProjectionAsync(
                historicalPath,
                "image.png",
                "image/png",
                AttachmentCategory.Image,
                inlineImages: true,
                size: 3,
                TestContext.Current.CancellationToken);

            var finalName = Path.GetFileName(historicalPath);
            Assert.Matches("^image_hist_[0-9a-f]{16}\\.png$", finalName);
            Assert.Contains($"path=\"inbox/{finalName}\"", projection.Line, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(sessionDir, "inbox", finalName)));
        }
        finally
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }
}
