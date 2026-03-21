using Xunit;

namespace Netclaw.Security.Tests;

public sealed class MagicByteValidatorTests
{
    // Real PNG header bytes
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00];

    // Real JPEG header bytes
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    // Real GIF header bytes (GIF89a)
    private static readonly byte[] GifHeader = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00];

    // Real WebP header bytes (RIFF....WEBP)
    private static readonly byte[] WebpHeader = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56];

    // Windows EXE (MZ header)
    private static readonly byte[] ExeHeader = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];

    // Linux ELF header
    private static readonly byte[] ElfHeader = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00];

    // Shebang script
    private static readonly byte[] ShebangHeader = [0x23, 0x21, 0x2F, 0x62, 0x69, 0x6E, 0x2F, 0x73, 0x68]; // #!/bin/sh

    [Fact]
    public void Validate_PngWithValidBytes_Allowed()
    {
        var result = MagicByteValidator.Validate(PngHeader, "image/png", "photo.png");

        Assert.True(result.IsAllowed);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Validate_JpegWithValidBytes_Allowed()
    {
        var result = MagicByteValidator.Validate(JpegHeader, "image/jpeg", "photo.jpg");

        Assert.True(result.IsAllowed);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Validate_JpegWithJpegExtension_Allowed()
    {
        var result = MagicByteValidator.Validate(JpegHeader, "image/jpeg", "photo.jpeg");

        Assert.True(result.IsAllowed);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Validate_GifWithValidBytes_Allowed()
    {
        var result = MagicByteValidator.Validate(GifHeader, "image/gif", "animation.gif");

        Assert.True(result.IsAllowed);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Validate_WebpWithValidBytes_Allowed()
    {
        var result = MagicByteValidator.Validate(WebpHeader, "image/webp", "photo.webp");

        Assert.True(result.IsAllowed);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Validate_EmptyContent_Rejected()
    {
        var result = MagicByteValidator.Validate(ReadOnlySpan<byte>.Empty, "image/png", "empty.png");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.EmptyContent, result.Error);
    }

    [Fact]
    public void Validate_ExecutableBytes_AlwaysRejected()
    {
        // EXE bytes disguised as PNG
        var result = MagicByteValidator.Validate(ExeHeader, "image/png", "photo.png");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.ExecutableContent, result.Error);
    }

    [Fact]
    public void Validate_ElfExecutable_AlwaysRejected()
    {
        var result = MagicByteValidator.Validate(ElfHeader, "image/png", "photo.png");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.ExecutableContent, result.Error);
    }

    [Fact]
    public void Validate_ShebangScript_AlwaysRejected()
    {
        var result = MagicByteValidator.Validate(ShebangHeader, "image/png", "photo.png");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.ExecutableContent, result.Error);
    }

    [Fact]
    public void Validate_PdfMimeType_RejectedAsUnrecognized()
    {
        // PDF is not in image-only allowlist
        byte[] pdfBytes = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34]; // %PDF-1.4
        var result = MagicByteValidator.Validate(pdfBytes, "application/pdf", "doc.pdf");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.UnrecognizedFileType, result.Error);
    }

    [Fact]
    public void Validate_MimeTypeMismatch_ExtensionDoesNotMatchDeclaredType()
    {
        // PNG bytes but declared as JPEG
        var result = MagicByteValidator.Validate(PngHeader, "image/jpeg", "photo.png");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_MagicByteMismatch_JpegBytesWithPngDeclaration()
    {
        // JPEG bytes but declared as PNG with .png extension
        var result = MagicByteValidator.Validate(JpegHeader, "image/png", "photo.png");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_UnknownExtension_Rejected()
    {
        var result = MagicByteValidator.Validate(PngHeader, "image/png", "photo.bmp");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.UnrecognizedFileType, result.Error);
    }

    [Fact]
    public void Validate_FileTooLarge_Rejected()
    {
        var policy = new ContentPolicy { MaxFileSizeBytes = 10 };
        var result = MagicByteValidator.Validate(PngHeader, "image/png", "photo.png", policy);

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.FileTooLarge, result.Error);
    }

    [Fact]
    public void HasExecutableSignature_DetectsAllKnownSignatures()
    {
        Assert.True(MagicByteValidator.HasExecutableSignature(ExeHeader));
        Assert.True(MagicByteValidator.HasExecutableSignature(ElfHeader));
        Assert.True(MagicByteValidator.HasExecutableSignature(ShebangHeader));
        Assert.False(MagicByteValidator.HasExecutableSignature(PngHeader));
        Assert.False(MagicByteValidator.HasExecutableSignature(JpegHeader));
    }

    [Theory]
    [InlineData(nameof(PngHeader), "image/png")]
    [InlineData(nameof(JpegHeader), "image/jpeg")]
    [InlineData(nameof(GifHeader), "image/gif")]
    [InlineData(nameof(WebpHeader), "image/webp")]
    public void DetectMimeType_identifies_supported_image_types(string headerField, string expectedMime)
    {
        var header = headerField switch
        {
            nameof(PngHeader) => PngHeader,
            nameof(JpegHeader) => JpegHeader,
            nameof(GifHeader) => GifHeader,
            nameof(WebpHeader) => WebpHeader,
            _ => throw new ArgumentException(headerField)
        };

        Assert.Equal(expectedMime, MagicByteValidator.DetectMimeType(header));
    }

    [Fact]
    public void DetectMimeType_returns_null_for_unknown_content()
    {
        byte[] randomBytes = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D];
        Assert.Null(MagicByteValidator.DetectMimeType(randomBytes));
    }

    [Fact]
    public void DetectMimeType_returns_null_for_empty_or_short_content()
    {
        Assert.Null(MagicByteValidator.DetectMimeType(ReadOnlySpan<byte>.Empty));
        Assert.Null(MagicByteValidator.DetectMimeType(new byte[] { 0xFF }));
        Assert.Null(MagicByteValidator.DetectMimeType(new byte[] { 0xFF, 0xD8 }));
    }
}
