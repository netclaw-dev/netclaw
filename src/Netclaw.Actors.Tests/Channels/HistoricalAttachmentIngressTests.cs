// -----------------------------------------------------------------------
// <copyright file="HistoricalAttachmentIngressTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Tests for the shared historical-attachment security gate that Slack, Discord,
/// and Mattermost run on both the fresh-download and the inbox cache-hit paths.
/// </summary>
public sealed class HistoricalAttachmentIngressTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] WindowsExecutableBytes = [0x4D, 0x5A, 0x90, 0x00];

    private static ChannelAttachmentPolicy ImagePolicy => new()
    {
        AllowedCategories = [AttachmentCategory.Image],
        MaxFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes,
        MaxFilesPerMessage = 10
    };

    private static IContentScanner Scanner => new MagicByteContentScanner(new ContentPolicy());

    private static string WriteTempFile(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task ScanAndVerify_uses_verified_mime_not_declared_for_octet_stream_png()
    {
        // A PNG that the transport mislabeled as application/octet-stream must
        // be promoted to its scanner-verified image/png MIME — this is the
        // exact value the cache-hit path now serves instead of the raw declared one.
        var path = WriteTempFile(PngBytes, ".png");
        try
        {
            var outcome = await HistoricalAttachmentIngress.ScanAndVerifyAsync(
                Scanner,
                path,
                "pic.png",
                new DeclaredMimeType("application/octet-stream"),
                TrustAudience.Public,
                ImagePolicy,
                TimeSpan.FromSeconds(5),
                NullLogger.Instance,
                TestContext.Current.CancellationToken);

            var verified = Assert.IsType<HistoricalAttachmentIngress.ScanOutcome.Verified>(outcome);
            Assert.Equal("image/png", verified.MimeType.Value);
            Assert.Equal(AttachmentCategory.Image, verified.Category);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ScanAndVerify_rejects_executable_spoofed_as_png()
    {
        // A Windows executable renamed to .png and declared image/png must be
        // rejected by the shared gate regardless of the cache/download path.
        var path = WriteTempFile(WindowsExecutableBytes, ".png");
        try
        {
            var outcome = await HistoricalAttachmentIngress.ScanAndVerifyAsync(
                Scanner,
                path,
                "evil.png",
                new DeclaredMimeType("image/png"),
                TrustAudience.Public,
                ImagePolicy,
                TimeSpan.FromSeconds(5),
                NullLogger.Instance,
                TestContext.Current.CancellationToken);

            var rejected = Assert.IsType<HistoricalAttachmentIngress.ScanOutcome.Rejected>(outcome);
            Assert.Contains("attachment rejected", rejected.Note.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ScanAndVerify_rejects_when_verified_category_not_allowed_by_audience()
    {
        // The file is a valid PNG, but the audience policy does not allow the
        // Image category — the verified-category gate must reject it.
        var path = WriteTempFile(PngBytes, ".png");
        var pdfOnlyPolicy = new ChannelAttachmentPolicy
        {
            AllowedCategories = [AttachmentCategory.Pdf],
            MaxFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes,
            MaxFilesPerMessage = 10
        };
        try
        {
            var outcome = await HistoricalAttachmentIngress.ScanAndVerifyAsync(
                Scanner,
                path,
                "pic.png",
                new DeclaredMimeType("image/png"),
                TrustAudience.Team,
                pdfOnlyPolicy,
                TimeSpan.FromSeconds(5),
                NullLogger.Instance,
                TestContext.Current.CancellationToken);

            var rejected = Assert.IsType<HistoricalAttachmentIngress.ScanOutcome.Rejected>(outcome);
            Assert.Contains("category not allowed", rejected.Note.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
