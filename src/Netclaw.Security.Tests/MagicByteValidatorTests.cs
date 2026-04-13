using System.Collections.Frozen;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class MagicByteValidatorTests
{
    // ── Image signatures ──────────────────────────────────────────────────
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] GifHeader = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00];
    private static readonly byte[] WebpHeader = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56];

    // ── Document / PDF signatures ─────────────────────────────────────────
    // %PDF-1.4 followed by a tiny catalog so the header has real-looking bytes
    private static readonly byte[] PdfHeader =
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"u8.ToArray();

    // ZIP local file header (PK\x03\x04) — prefix of OOXML docx/xlsx/pptx and ZIP archives
    private static readonly byte[] ZipHeader =
        [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00];

    // OLE Compound Document — legacy .doc/.xls/.ppt
    private static readonly byte[] OleHeader =
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00];

    // RTF header: {\rtf1\ansi...
    private static readonly byte[] RtfHeader = "{\\rtf1\\ansi\\deff0"u8.ToArray();

    private static readonly byte[] PlainTextBytes = "Hello, this is a plain text file with no magic signature.\n"u8.ToArray();

    // ── Archive signatures ────────────────────────────────────────────────
    private static readonly byte[] SevenZipHeader = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04];
    private static readonly byte[] GzipHeader = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00];
    // bzip2: "BZh" + block size '9' + 6-byte BCD-Pi compressed block header (31 41 59 26 53 59)
    private static readonly byte[] Bzip2Header =
        [0x42, 0x5A, 0x68, 0x39, 0x31, 0x41, 0x59, 0x26, 0x53, 0x59];
    private static readonly byte[] XzHeader = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00, 0x04];

    // ── Media signatures ──────────────────────────────────────────────────
    // ISO BMFF: 4 bytes box size, "ftyp", brand, minor version, compatible brands
    private static readonly byte[] Mp4FtypHeader =
        [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32, 0x00, 0x00, 0x00, 0x00];
    // RIFF....WAVE
    private static readonly byte[] WavHeader =
        [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20];
    // RIFF....AVI (trailing space)
    private static readonly byte[] AviHeader =
        [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x41, 0x56, 0x49, 0x20, 0x4C, 0x49, 0x53, 0x54];
    // ID3v2 tag prefix (used by most MP3s)
    private static readonly byte[] Mp3Id3Header = [0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00];
    // MP3 raw frame sync: 0xFF 0xFB MPEG-1 Layer 3
    private static readonly byte[] Mp3FrameHeader = [0xFF, 0xFB, 0x90, 0x00];
    // Ogg container
    private static readonly byte[] OggHeader = [0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00];
    // Matroska / WebM EBML header
    private static readonly byte[] EbmlHeader = [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81];

    // ── Executable signatures ─────────────────────────────────────────────
    private static readonly byte[] ExeHeader = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
    private static readonly byte[] ElfHeader = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00];
    private static readonly byte[] ShebangHeader = [0x23, 0x21, 0x2F, 0x62, 0x69, 0x6E, 0x2F, 0x73, 0x68]; // #!/bin/sh

    // ── Images ────────────────────────────────────────────────────────────

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

    // ── PDF ───────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_PdfWithValidMagicBytes_Allowed()
    {
        var result = MagicByteValidator.Validate(PdfHeader, "application/pdf", "report.pdf");

        Assert.True(result.IsAllowed);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Validate_PdfExtensionWithPngPayload_MimeTypeMismatch()
    {
        var result = MagicByteValidator.Validate(PngHeader, "application/pdf", "fake.pdf");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_PdfPayloadDeclaredAsPng_MimeTypeMismatch()
    {
        var result = MagicByteValidator.Validate(PdfHeader, "image/png", "photo.png");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    // ── OOXML / OLE / ODF documents ───────────────────────────────────────

    [Fact]
    public void Validate_DocxWithZipMagic_Allowed()
    {
        var result = MagicByteValidator.Validate(
            ZipHeader,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "report.docx");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_XlsxWithZipMagic_Allowed()
    {
        var result = MagicByteValidator.Validate(
            ZipHeader,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "budget.xlsx");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_PptxWithZipMagic_Allowed()
    {
        var result = MagicByteValidator.Validate(
            ZipHeader,
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "slides.pptx");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_OdtWithZipMagic_Allowed()
    {
        var result = MagicByteValidator.Validate(
            ZipHeader,
            "application/vnd.oasis.opendocument.text",
            "notes.odt");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_DocxWithOleHeader_MimeTypeMismatch()
    {
        // Old .doc bytes declared as .docx (OOXML) — mismatch
        var result = MagicByteValidator.Validate(
            OleHeader,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "report.docx");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_LegacyDocWithOleHeader_Allowed()
    {
        var result = MagicByteValidator.Validate(OleHeader, "application/msword", "legacy.doc");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_LegacyXlsWithOleHeader_Allowed()
    {
        var result = MagicByteValidator.Validate(OleHeader, "application/vnd.ms-excel", "legacy.xls");

        Assert.True(result.IsAllowed);
    }

    // ── Plain / structured text ───────────────────────────────────────────

    [Fact]
    public void Validate_PlainTextWithoutMagic_Allowed()
    {
        var result = MagicByteValidator.Validate(PlainTextBytes, "text/plain", "notes.txt");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_MarkdownAllowed()
    {
        var result = MagicByteValidator.Validate(PlainTextBytes, "text/markdown", "readme.md");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_CsvAllowed()
    {
        var result = MagicByteValidator.Validate(PlainTextBytes, "text/csv", "data.csv");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_JsonAllowed()
    {
        var json = "{\"ok\":true}\n"u8.ToArray();
        var result = MagicByteValidator.Validate(json, "application/json", "payload.json");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_PlainTextWithExecutablePrefix_RejectedAsExecutable()
    {
        // Someone renames a Windows EXE to .txt — the executable pre-check
        // fires regardless of declared MIME, protecting the text/plain path.
        var result = MagicByteValidator.Validate(ExeHeader, "text/plain", "notes.txt");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.ExecutableContent, result.Error);
    }

    [Fact]
    public void Validate_RtfAllowed()
    {
        var result = MagicByteValidator.Validate(RtfHeader, "application/rtf", "document.rtf");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_RtfWithBogusMagic_MimeTypeMismatch()
    {
        var result = MagicByteValidator.Validate(PngHeader, "application/rtf", "fake.rtf");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    // ── Archives ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ZipAllowed()
    {
        var result = MagicByteValidator.Validate(ZipHeader, "application/zip", "archive.zip");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_SevenZipAllowed()
    {
        var result = MagicByteValidator.Validate(SevenZipHeader, "application/x-7z-compressed", "archive.7z");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_GzipAllowed()
    {
        var result = MagicByteValidator.Validate(GzipHeader, "application/gzip", "log.gz");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_Bzip2Allowed()
    {
        var result = MagicByteValidator.Validate(Bzip2Header, "application/x-bzip2", "backup.bz2");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_XzAllowed()
    {
        var result = MagicByteValidator.Validate(XzHeader, "application/x-xz", "backup.xz");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_ZipDeclaredAs7z_MimeTypeMismatch()
    {
        var result = MagicByteValidator.Validate(ZipHeader, "application/x-7z-compressed", "fake.7z");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    // ── Media ─────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Mp4WithFtypBox_Allowed()
    {
        var result = MagicByteValidator.Validate(Mp4FtypHeader, "video/mp4", "clip.mp4");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_QuickTimeWithFtypBox_Allowed()
    {
        var result = MagicByteValidator.Validate(Mp4FtypHeader, "video/quicktime", "clip.mov");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_Mp3WithId3Tag_Allowed()
    {
        var result = MagicByteValidator.Validate(Mp3Id3Header, "audio/mpeg", "song.mp3");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_Mp3WithFrameSync_Allowed()
    {
        var result = MagicByteValidator.Validate(Mp3FrameHeader, "audio/mpeg", "song.mp3");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_WavAllowed()
    {
        var result = MagicByteValidator.Validate(WavHeader, "audio/wav", "sound.wav");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_AviAllowed()
    {
        var result = MagicByteValidator.Validate(AviHeader, "video/x-msvideo", "clip.avi");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_OggAllowed()
    {
        var result = MagicByteValidator.Validate(OggHeader, "audio/ogg", "sound.ogg");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_WebmAllowed()
    {
        var result = MagicByteValidator.Validate(EbmlHeader, "video/webm", "clip.webm");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_MatroskaAllowed()
    {
        var result = MagicByteValidator.Validate(EbmlHeader, "video/x-matroska", "clip.mkv");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_WavDeclaredAsMp4_MimeTypeMismatch()
    {
        // Both are "RIFF" but differ at offset 8 — WAV at offset 8 is "WAVE",
        // MP4 at offset 4 is "ftyp". Make sure the stricter MP4 check rejects.
        var result = MagicByteValidator.Validate(WavHeader, "video/mp4", "fake.mp4");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    // ── Cross-cutting ─────────────────────────────────────────────────────

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
    public void Validate_UnknownMimeType_Rejected()
    {
        // application/octet-stream is classified as Other and not in the rule table
        var result = MagicByteValidator.Validate(PngHeader, "application/octet-stream", "data.bin");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.UnrecognizedFileType, result.Error);
    }

    [Fact]
    public void Validate_MimeTypeMismatch_ExtensionDoesNotMatchDeclaredType()
    {
        // PNG bytes but declared as JPEG → mismatch (rule lookup finds jpeg,
        // extension .png is not in jpeg's {.jpg,.jpeg} set)
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
    public void Validate_UnknownExtensionForKnownMime_MimeTypeMismatch()
    {
        // PNG bytes with MIME image/png, but filename is .bmp — extension
        // is not in image/png's extension set, so the rule rejects it
        // as a mismatch (not UnrecognizedFileType — the MIME is known).
        var result = MagicByteValidator.Validate(PngHeader, "image/png", "photo.bmp");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_PolicyDisallowsKnownMime_Rejected()
    {
        // Operator restricts policy to PNG only; PDF is known to the validator
        // but disallowed by the runtime policy layer.
        var policy = new ContentPolicy
        {
            AllowedMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "image/png" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        };

        var result = MagicByteValidator.Validate(PdfHeader, "application/pdf", "report.pdf", policy);

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
        Assert.False(MagicByteValidator.HasExecutableSignature(PdfHeader));
    }

    [Theory]
    [InlineData(nameof(PngHeader), "image/png")]
    [InlineData(nameof(JpegHeader), "image/jpeg")]
    [InlineData(nameof(GifHeader), "image/gif")]
    [InlineData(nameof(WebpHeader), "image/webp")]
    [InlineData(nameof(PdfHeader), "application/pdf")]
    [InlineData(nameof(ZipHeader), "application/zip")]
    [InlineData(nameof(OleHeader), "application/x-ole-compound-document")]
    [InlineData(nameof(Mp4FtypHeader), "video/mp4")]
    [InlineData(nameof(WavHeader), "audio/wav")]
    [InlineData(nameof(AviHeader), "video/x-msvideo")]
    [InlineData(nameof(EbmlHeader), "video/webm")]
    [InlineData(nameof(OggHeader), "audio/ogg")]
    [InlineData(nameof(Mp3Id3Header), "audio/mpeg")]
    public void DetectMimeType_identifies_supported_signature_families(string headerField, string expectedMime)
    {
        var header = headerField switch
        {
            nameof(PngHeader) => PngHeader,
            nameof(JpegHeader) => JpegHeader,
            nameof(GifHeader) => GifHeader,
            nameof(WebpHeader) => WebpHeader,
            nameof(PdfHeader) => PdfHeader,
            nameof(ZipHeader) => ZipHeader,
            nameof(OleHeader) => OleHeader,
            nameof(Mp4FtypHeader) => Mp4FtypHeader,
            nameof(WavHeader) => WavHeader,
            nameof(AviHeader) => AviHeader,
            nameof(EbmlHeader) => EbmlHeader,
            nameof(OggHeader) => OggHeader,
            nameof(Mp3Id3Header) => Mp3Id3Header,
            _ => throw new ArgumentException(headerField)
        };

        Assert.Equal(expectedMime, MagicByteValidator.DetectMimeType(header));
    }

    // ── Hardening: reject minimum-magic polyglots ─────────────────────────

    [Fact]
    public void Validate_RejectsJpegWithStuffedMarker()
    {
        // FF D8 FF 00 — FF 00 is an escaped stuffed byte inside JPEG data,
        // not a valid JPEG start-of-image marker. A minimum-magic checker
        // would accept this as JPEG; the hardened check rejects it.
        byte[] bogusJpeg = [0xFF, 0xD8, 0xFF, 0x00, 0x10, 0x4A];
        var result = MagicByteValidator.Validate(bogusJpeg, "image/jpeg", "bogus.jpg");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsGifWithBogusVersion()
    {
        // GIF8Xa — minimum check (GIF8 prefix) would accept; tightened check
        // requires '7a' or '9a' at bytes 4-5.
        byte[] bogusGif = [0x47, 0x49, 0x46, 0x38, 0x58, 0x61, 0x01];
        var result = MagicByteValidator.Validate(bogusGif, "image/gif", "bogus.gif");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsPdfWithoutVersionDigit()
    {
        // %PDF-X.0 — missing digit after the dash. Minimum 5-byte check
        // would accept; tightened check requires a digit + dot.
        byte[] bogusPdf = "%PDF-X.0\n"u8.ToArray();
        var result = MagicByteValidator.Validate(bogusPdf, "application/pdf", "bogus.pdf");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsRtfWithoutVersionDigit()
    {
        // {\rtfX — RTF spec requires a version digit after \rtf.
        byte[] bogusRtf = "{\\rtfX junk"u8.ToArray();
        var result = MagicByteValidator.Validate(bogusRtf, "application/rtf", "bogus.rtf");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsZipWithInvalidHeaderPair()
    {
        // PK\x03\x06 is not a valid ZIP local/central-dir/spanned marker.
        // Minimum check (PK + any of 03/05/07 + any of 04/06/08) would have
        // accepted this; tightened check requires the exact 4-byte pair.
        byte[] bogusZip = [0x50, 0x4B, 0x03, 0x06, 0x14, 0x00, 0x00, 0x00];
        var result = MagicByteValidator.Validate(bogusZip, "application/zip", "bogus.zip");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsGzipWithNonDeflateMethod()
    {
        // 1F 8B 01 — compression method 1 is reserved; only 0x08 (DEFLATE)
        // is defined by RFC 1952. Minimum 2-byte check would accept.
        byte[] bogusGzip = [0x1F, 0x8B, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];
        var result = MagicByteValidator.Validate(bogusGzip, "application/gzip", "bogus.gz");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsBzip2WithBogusBlockHeader()
    {
        // BZh9 + garbage where the BCD-Pi block header should be.
        byte[] bogusBzip2 = [0x42, 0x5A, 0x68, 0x39, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        var result = MagicByteValidator.Validate(bogusBzip2, "application/x-bzip2", "bogus.bz2");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsFtypWithNonAsciiMajorBrand()
    {
        // Valid "ftyp" box header but major brand bytes are not printable
        // ASCII — indicates a garbage or adversarial box payload.
        byte[] bogusFtyp =
            [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x00, 0x01, 0x02, 0x03];
        var result = MagicByteValidator.Validate(bogusFtyp, "video/mp4", "bogus.mp4");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsOggWithNonZeroVersion()
    {
        // OggS + version byte 0x01 — RFC 3533 requires version 0x00.
        byte[] bogusOgg = [0x4F, 0x67, 0x67, 0x53, 0x01, 0x02, 0x00, 0x00];
        var result = MagicByteValidator.Validate(bogusOgg, "audio/ogg", "bogus.ogg");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsMp3WithReservedLayer()
    {
        // 0xFF 0xF9 — top nibble F (12-bit sync OK), version bits 11
        // (MPEG-1 OK), but layer bits (2-1) = 00 → reserved per ISO/IEC
        // 11172-3. 0xF9 = 1111 1001, bits 2-1 = 00.
        byte[] bogusMp3 = [0xFF, 0xF9, 0x00, 0x00];
        var result = MagicByteValidator.Validate(bogusMp3, "audio/mpeg", "bogus.mp3");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsMp3With11BitSyncPolyglot()
    {
        // 0xFF 0xE0 — only 11-bit sync. The looser check accepted this;
        // the tightened 12-bit sync requires top nibble F, which rejects.
        byte[] bogusMp3 = [0xFF, 0xE0, 0x00, 0x00];
        var result = MagicByteValidator.Validate(bogusMp3, "audio/mpeg", "bogus.mp3");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
    }

    [Fact]
    public void Validate_RejectsId3WithBogusMajorVersion()
    {
        // ID3 + major version 99 — valid ID3v2 is 2, 3, or 4.
        byte[] bogusId3 = [0x49, 0x44, 0x33, 0x63, 0x00, 0x00, 0x00, 0x00];
        var result = MagicByteValidator.Validate(bogusId3, "audio/mpeg", "bogus.mp3");

        Assert.False(result.IsAllowed);
        Assert.Equal(ContentScanError.MimeTypeMismatch, result.Error);
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
