// -----------------------------------------------------------------------
// <copyright file="ContentVerificationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Direct coverage of the single content-verification decision shared by the
/// live and historical ingress pipelines.
/// </summary>
public sealed class ContentVerificationTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] WindowsExecutableBytes = [0x4D, 0x5A, 0x90, 0x00];

    private static IContentScanner RealScanner => new MagicByteContentScanner(new ContentPolicy());

    private static ChannelAttachmentPolicy Policy(params AttachmentCategory[] allowed) => new()
    {
        AllowedCategories = [.. allowed],
        MaxFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes,
        MaxFilesPerMessage = 10
    };

    private static string WriteTempFile(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Task<ContentVerificationResult> ResolveAsync(
        IContentScanner scanner, string path, string filename, string declaredMime, ChannelAttachmentPolicy policy)
        => ContentVerification.ResolveAsync(
            scanner, path, filename, new DeclaredMimeType(declaredMime), policy,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Verifies_octet_stream_png_against_verified_mime()
    {
        var path = WriteTempFile(PngBytes, ".png");
        try
        {
            var result = await ResolveAsync(RealScanner, path, "pic.png", "application/octet-stream", Policy(AttachmentCategory.Image));

            var verified = Assert.IsType<ContentVerificationResult.Verified>(result);
            Assert.Equal("image/png", verified.MimeType.Value);
            Assert.Equal(AttachmentCategory.Image, verified.Category);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Blocks_executable_spoofed_as_image()
    {
        var path = WriteTempFile(WindowsExecutableBytes, ".png");
        try
        {
            var result = await ResolveAsync(RealScanner, path, "evil.png", "image/png", Policy(AttachmentCategory.Image));

            Assert.IsType<ContentVerificationResult.ScanBlocked>(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Rejects_verified_category_not_allowed_by_audience()
    {
        var path = WriteTempFile(PngBytes, ".png");
        try
        {
            var result = await ResolveAsync(RealScanner, path, "pic.png", "image/png", Policy(AttachmentCategory.Pdf));

            var rejected = Assert.IsType<ContentVerificationResult.CategoryNotAllowed>(result);
            Assert.Equal("image/png", rejected.MimeType.Value);
            Assert.Equal(AttachmentCategory.Image, rejected.Category);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reports_scan_exception()
    {
        var result = await ResolveAsync(new ThrowingScanner(), "ignored", "x.png", "image/png", Policy(AttachmentCategory.Image));

        var threw = Assert.IsType<ContentVerificationResult.ScanThrew>(result);
        Assert.IsType<IOException>(threw.Exception);
    }

    [Fact]
    public async Task Reports_missing_verified_mime()
    {
        var result = await ResolveAsync(new AllowWithoutVerifyingScanner(), "ignored", "x.png", "image/png", Policy(AttachmentCategory.Image));

        Assert.IsType<ContentVerificationResult.MissingVerifiedMime>(result);
    }

    [Fact]
    public async Task Propagates_outer_cancellation_instead_of_reporting_a_scan_failure()
    {
        // Host/session shutdown cancels the outer token; that must propagate as
        // cancellation, not be masked as a ScanThrew rejection.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ContentVerification.ResolveAsync(
                new CancellationThrowingScanner(),
                "ignored",
                "x.png",
                new DeclaredMimeType("image/png"),
                Policy(AttachmentCategory.Image),
                TimeSpan.FromSeconds(5),
                cts.Token));
    }

    [Fact]
    public async Task Scan_cancellation_without_outer_cancel_is_treated_as_scan_failure()
    {
        // An OCE that is NOT from the outer token (e.g. the scan's own timeout)
        // must be reported as ScanThrew, not propagated as cancellation.
        var result = await ContentVerification.ResolveAsync(
            new CancellationThrowingScanner(),
            "ignored",
            "x.png",
            new DeclaredMimeType("image/png"),
            Policy(AttachmentCategory.Image),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.IsType<ContentVerificationResult.ScanThrew>(result);
    }

    private sealed class ThrowingScanner : IContentScanner
    {
        public Task<ContentScanResult> ScanAsync(ReadOnlyMemory<byte> content, string filename, string declaredMimeType, CancellationToken cancellationToken = default)
            => throw new IOException("scan failed");

        public Task<ContentScanResult> ScanFileAsync(string filePath, string filename, string declaredMimeType, CancellationToken cancellationToken = default)
            => throw new IOException("scan failed");
    }

    // Models a scanner that approves bytes but never reports a verified MIME —
    // the pipelines must treat that as a rejection, not an acceptance.
    private sealed class AllowWithoutVerifyingScanner : IContentScanner
    {
        public Task<ContentScanResult> ScanAsync(ReadOnlyMemory<byte> content, string filename, string declaredMimeType, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentScanResult(true));

        public Task<ContentScanResult> ScanFileAsync(string filePath, string filename, string declaredMimeType, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentScanResult(true));
    }

    private sealed class CancellationThrowingScanner : IContentScanner
    {
        public Task<ContentScanResult> ScanAsync(ReadOnlyMemory<byte> content, string filename, string declaredMimeType, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException();

        public Task<ContentScanResult> ScanFileAsync(string filePath, string filename, string declaredMimeType, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException();
    }
}
