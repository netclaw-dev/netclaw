// -----------------------------------------------------------------------
// <copyright file="MagicByteValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Frozen;
using Netclaw.Media;

namespace Netclaw.Security;

/// <summary>
/// Validates file content using magic byte (file signature) analysis.
/// Supports the categories advertised by <c>ChannelAttachmentPolicy</c>'s
/// <c>Team</c> / <c>Personal</c> audience defaults: images, PDFs, Office
/// documents (OOXML + legacy OLE), plain/rich text, common archives, and
/// common audio/video containers. Unknown declared MIME types are rejected
/// as <see cref="ContentScanError.UnrecognizedFileType"/>.
/// </summary>
public static class MagicByteValidator
{
    /// <summary>
    /// Magic byte signatures for executable content — always blocked,
    /// regardless of declared MIME type.
    /// </summary>
    private static readonly byte[][] ExecutableSignatures =
    [
        [0x4D, 0x5A],                   // MZ — Windows PE (EXE, DLL)
        [0x7F, 0x45, 0x4C, 0x46],       // ELF — Linux executables
        [0xCF, 0xFA, 0xED, 0xFE],       // Mach-O 64-bit
        [0xCE, 0xFA, 0xED, 0xFE],       // Mach-O 32-bit
        [0xCA, 0xFE, 0xBA, 0xBE],       // Java CLASS or Mach-O Universal
        [0x23, 0x21]                     // #! — Shebang (shell scripts)
    ];

    /// <summary>
    /// Matcher delegate for a signature rule. Uses a typed delegate instead
    /// of <c>Func&lt;ReadOnlySpan&lt;byte&gt;, bool&gt;</c> because
    /// <see cref="ReadOnlySpan{T}"/> cannot be a generic type argument.
    /// </summary>
    private delegate bool SignatureMatcher(ReadOnlySpan<byte> content);

    /// <summary>
    /// Matcher that unconditionally accepts any content. Used for text-like
    /// MIMEs that have no signature at offset 0; the executable pre-check
    /// still runs first, so an <c>MZ</c>- or <c>#!</c>-prefixed "text" file
    /// is still rejected as <see cref="ContentScanError.ExecutableContent"/>.
    /// </summary>
    private static readonly SignatureMatcher AnyContent = static _ => true;

    /// <summary>
    /// MIME → byte-signature matcher. The matcher is the only piece the security
    /// layer owns; the set of filename extensions permitted for each MIME comes
    /// from <see cref="MimeTypeCatalog"/> (via <see cref="MimeTypeCatalog.ExtensionMatches"/>),
    /// so the extension table is defined once in the catalog and cannot drift
    /// against the validator.
    /// </summary>
    private static readonly FrozenDictionary<string, SignatureMatcher> SignatureMatchers =
        BuildMatchers().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MIME types for which the scanner has a native byte-signature matcher.
    /// Kept aligned with <see cref="MimeTypeCatalog.GetNativeSignatureValidatedMimeTypes"/>.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedMimeTypes => SignatureMatchers.Keys;

    /// <summary>
    /// Validates a file using only its header bytes and total size. Used by
    /// the streaming download path where the full file is on disk and only
    /// the first N bytes are read into memory for signature checking.
    /// </summary>
    /// <param name="header">First N bytes of the file (64 bytes is sufficient for all known signatures).</param>
    /// <param name="totalFileSize">Total file size from <see cref="System.IO.FileInfo.Length"/>.</param>
    /// <param name="declaredMimeType">MIME type declared by the channel transport.</param>
    /// <param name="filename">Original filename including extension.</param>
    /// <param name="policy">Content policy; null uses default.</param>
    public static ContentScanResult ValidateFromHeader(
        ReadOnlySpan<byte> header,
        long totalFileSize,
        string declaredMimeType,
        string filename,
        ContentPolicy? policy = null)
    {
        if (totalFileSize == 0)
        {
            return ContentScanResult.Rejected(
                ContentScanError.EmptyContent,
                "File is empty");
        }

        var effectivePolicy = policy ?? new ContentPolicy();

        if (totalFileSize > effectivePolicy.MaxFileSizeBytes)
        {
            return ContentScanResult.Rejected(
                ContentScanError.FileTooLarge,
                $"File exceeds maximum size of {effectivePolicy.MaxFileSizeBytes / (1024 * 1024)} MiB");
        }

        if (HasExecutableSignature(header))
        {
            var detectedType = DetectMimeType(header);
            return ContentScanResult.Rejected(
                ContentScanError.ExecutableContent,
                "Executable content detected",
                detectedType is not null ? new MimeType(detectedType) : null);
        }

        var extension = Path.GetExtension(filename);
        if (string.IsNullOrEmpty(extension))
        {
            return ContentScanResult.Rejected(
                ContentScanError.UnrecognizedFileType,
                "File has no extension");
        }

        var effectiveMimeType = MimeTypeCatalog.NormalizeDeclaredForExtension(declaredMimeType, extension);

        if (!SignatureMatchers.TryGetValue(effectiveMimeType.Value, out var matcher))
        {
            return ContentScanResult.Rejected(
                ContentScanError.UnrecognizedFileType,
                $"MIME type '{effectiveMimeType}' is not supported by the content scanner");
        }

        if (!effectivePolicy.AllowedMimeTypes.Contains(effectiveMimeType.Value))
        {
            return ContentScanResult.Rejected(
                ContentScanError.UnrecognizedFileType,
                $"MIME type '{effectiveMimeType}' is not allowed by policy");
        }

        if (!MimeTypeCatalog.ExtensionMatches(effectiveMimeType, extension))
        {
            var detected = DetectMimeType(header);
            var detectedMimeType = detected is not null ? new MimeType(detected) : (MimeType?)null;
            if (detectedMimeType is not null
                && SignatureMatchers.ContainsKey(detectedMimeType.Value.Value)
                && MimeTypeCatalog.ExtensionMatches(detectedMimeType.Value, extension)
                && effectivePolicy.AllowedMimeTypes.Contains(detectedMimeType.Value.Value))
            {
                return ContentScanResult.Allowed(detectedMimeType.Value);
            }

            return ContentScanResult.Rejected(
                ContentScanError.MimeTypeMismatch,
                $"Extension '{extension}' does not match declared type '{effectiveMimeType}'",
                detectedMimeType);
        }

        if (!matcher(header))
        {
            var detectedMimeType = DetectMimeType(header);
            return ContentScanResult.Rejected(
                ContentScanError.MimeTypeMismatch,
                $"Content is not a valid {effectiveMimeType} file",
                detectedMimeType is not null ? new MimeType(detectedMimeType) : null);
        }

        return ContentScanResult.Allowed(effectiveMimeType);
    }

    /// <summary>
    /// Validates file content against its declared MIME type and filename.
    /// Delegates to <see cref="ValidateFromHeader"/> using the full content as the header.
    /// </summary>
    public static ContentScanResult Validate(
        ReadOnlySpan<byte> content,
        string declaredMimeType,
        string filename,
        ContentPolicy? policy = null)
    {
        return ValidateFromHeader(content, content.Length, declaredMimeType, filename, policy);
    }

    /// <summary>
    /// Detects MIME type from magic bytes for supported signature families.
    /// Used to populate the <c>DetectedType</c> field on rejection results so
    /// operators can see "looks like a PDF but was declared as an image".
    /// Returns null for unrecognized content.
    /// </summary>
    public static string? DetectMimeType(ReadOnlySpan<byte> content)
    {
        if (IsPng(content)) return "image/png";
        if (IsJpeg(content)) return "image/jpeg";
        if (IsGif(content)) return "image/gif";
        if (IsWebp(content)) return "image/webp";
        if (IsBmp(content)) return "image/bmp";
        if (IsTiff(content)) return "image/tiff";
        if (IsPdf(content)) return "application/pdf";
        if (IsOle(content)) return "application/x-ole-compound-document";
        if (IsRtf(content)) return "application/rtf";
        if (Is7z(content)) return "application/x-7z-compressed";
        if (IsGzip(content)) return "application/gzip";
        if (IsBzip2(content)) return "application/x-bzip2";
        if (IsXz(content)) return "application/x-xz";
        if (IsZip(content)) return "application/zip";
        if (IsWav(content)) return "audio/wav";
        if (IsAvi(content)) return "video/x-msvideo";
        if (IsFtyp(content)) return "video/mp4";
        if (IsEbml(content)) return "video/webm";
        if (IsOgg(content)) return "audio/ogg";
        if (IsMp3FrameOrId3(content)) return "audio/mpeg";
        return null;
    }

    public static bool HasExecutableSignature(ReadOnlySpan<byte> content)
    {
        if (content.Length < 2)
            return false;

        foreach (var signature in ExecutableSignatures)
        {
            if (content.Length >= signature.Length && content[..signature.Length].SequenceEqual(signature))
                return true;
        }

        return false;
    }

    // ── Matcher table ─────────────────────────────────────────────────────
    // MIME → byte-signature matcher only. Permitted extensions live in
    // MimeTypeCatalog; this table must stay aligned with the catalog's
    // native-signature-validated set (enforced by a test).

    private static Dictionary<string, SignatureMatcher> BuildMatchers() => new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        ["image/png"] = IsPng,
        ["image/jpeg"] = IsJpeg,
        ["image/gif"] = IsGif,
        ["image/webp"] = IsWebp,
        ["image/bmp"] = IsBmp,
        ["image/tiff"] = IsTiff,

        // PDF
        ["application/pdf"] = IsPdf,

        // OOXML (ZIP-based) — docx/xlsx/pptx
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = IsZip,
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = IsZip,
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = IsZip,

        // OpenDocument (also ZIP-based)
        ["application/vnd.oasis.opendocument.text"] = IsZip,
        ["application/vnd.oasis.opendocument.spreadsheet"] = IsZip,
        ["application/vnd.oasis.opendocument.presentation"] = IsZip,

        // Legacy OLE Compound Document Office formats
        ["application/msword"] = IsOle,
        ["application/vnd.ms-excel"] = IsOle,
        ["application/vnd.ms-powerpoint"] = IsOle,

        // Plain/structured text — no signature, but executable pre-check
        // still blocks MZ / ELF / shebang payloads.
        ["text/plain"] = AnyContent,
        ["text/markdown"] = AnyContent,
        ["text/csv"] = AnyContent,
        ["text/tab-separated-values"] = AnyContent,
        ["text/html"] = AnyContent,
        ["application/json"] = AnyContent,
        ["application/xml"] = AnyContent,
        ["application/yaml"] = AnyContent,

        // Rich text
        ["application/rtf"] = IsRtf,

        // Archives
        ["application/zip"] = IsZip,
        ["application/x-7z-compressed"] = Is7z,
        ["application/gzip"] = IsGzip,
        ["application/x-bzip2"] = IsBzip2,
        ["application/x-xz"] = IsXz,

        // Audio
        ["audio/mpeg"] = IsMp3FrameOrId3,
        ["audio/mp4"] = IsFtyp,
        ["audio/wav"] = IsWav,
        ["audio/ogg"] = IsOgg,

        // Video
        ["video/mp4"] = IsFtyp,
        ["video/quicktime"] = IsFtyp,
        ["video/webm"] = IsEbml,
        ["video/x-matroska"] = IsEbml,
        ["video/x-msvideo"] = IsAvi
    };

    // ── Signature matchers ────────────────────────────────────────────────
    // Each matcher validates more than the minimum prefix where a cheap
    // follow-on check is available. The goal is to reject polyglots and
    // hand-forged headers that match the minimum magic but have bogus
    // version/layer/brand fields, without false-rejecting real files.

    private static bool IsPng(ReadOnlySpan<byte> c) =>
        c.Length >= 8 &&
        c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47 &&
        c[4] == 0x0D && c[5] == 0x0A && c[6] == 0x1A && c[7] == 0x0A;

    /// <summary>
    /// JPEG: SOI (<c>FF D8</c>) followed by a marker byte. The third byte
    /// must look like a JPEG marker (high nibble <c>F</c>, low nibble not
    /// <c>0</c> or <c>F</c> — <c>FF 00</c> is a stuffed byte inside data,
    /// <c>FF FF</c> is fill).
    /// </summary>
    private static bool IsJpeg(ReadOnlySpan<byte> c)
    {
        if (c.Length < 3) return false;
        if (c[0] != 0xFF || c[1] != 0xD8 || c[2] != 0xFF) return false;
        if (c.Length < 4) return false;
        var marker = c[3];
        return marker != 0x00 && marker != 0xFF;
    }

    /// <summary>
    /// GIF: <c>GIF87a</c> or <c>GIF89a</c> — validate all 6 bytes rather
    /// than just the <c>GIF8</c> prefix so <c>GIF8Xa</c> prefixes from
    /// adversarial content are rejected.
    /// </summary>
    private static bool IsGif(ReadOnlySpan<byte> c)
    {
        if (c.Length < 6) return false;
        if (c[0] != 0x47 || c[1] != 0x49 || c[2] != 0x46 || c[3] != 0x38) return false;
        if (c[5] != 0x61) return false; // 'a'
        return c[4] == 0x37 || c[4] == 0x39; // '7' or '9'
    }

    private static bool IsRiff(ReadOnlySpan<byte> c) =>
        c.Length >= 4 &&
        c[0] == 0x52 && c[1] == 0x49 && c[2] == 0x46 && c[3] == 0x46;

    private static bool IsWebp(ReadOnlySpan<byte> c) =>
        IsRiff(c) && c.Length >= 12 &&
        c[8] == 0x57 && c[9] == 0x45 && c[10] == 0x42 && c[11] == 0x50;

    /// <summary>
    /// BMP: <c>BM</c> magic, then the DIB header size (LE uint32 at offset 14)
    /// must be one of the known header sizes. Validating the DIB size rejects
    /// arbitrary "BM"-prefixed payloads.
    /// </summary>
    private static bool IsBmp(ReadOnlySpan<byte> c)
    {
        if (c.Length < 18) return false;
        if (c[0] != 0x42 || c[1] != 0x4D) return false; // "BM"
        uint dibHeaderSize = (uint)(c[14] | (c[15] << 8) | (c[16] << 16) | (c[17] << 24));
        return dibHeaderSize is 12 or 40 or 52 or 56 or 64 or 108 or 124;
    }

    /// <summary>
    /// TIFF: little-endian (<c>II</c> + <c>0x2A 0x00</c>) or big-endian
    /// (<c>MM</c> + <c>0x00 0x2A</c>) byte-order mark plus the 42 magic.
    /// </summary>
    private static bool IsTiff(ReadOnlySpan<byte> c)
    {
        if (c.Length < 4) return false;
        return (c[0] == 0x49 && c[1] == 0x49 && c[2] == 0x2A && c[3] == 0x00)
            || (c[0] == 0x4D && c[1] == 0x4D && c[2] == 0x00 && c[3] == 0x2A);
    }

    private static bool IsWav(ReadOnlySpan<byte> c) =>
        IsRiff(c) && c.Length >= 12 &&
        c[8] == 0x57 && c[9] == 0x41 && c[10] == 0x56 && c[11] == 0x45;

    private static bool IsAvi(ReadOnlySpan<byte> c) =>
        IsRiff(c) && c.Length >= 12 &&
        c[8] == 0x41 && c[9] == 0x56 && c[10] == 0x49 && c[11] == 0x20;

    /// <summary>
    /// PDF: <c>%PDF-</c> followed by a version digit and a dot. A minimal
    /// valid PDF starts with <c>%PDF-1.0</c> through <c>%PDF-2.0</c>; the
    /// tightened check requires byte 5 to be an ASCII digit and byte 6 to
    /// be a dot, rejecting <c>%PDF-xx</c> garbage headers.
    /// </summary>
    private static bool IsPdf(ReadOnlySpan<byte> c)
    {
        if (c.Length < 7) return false;
        if (c[0] != 0x25 || c[1] != 0x50 || c[2] != 0x44 || c[3] != 0x46 || c[4] != 0x2D)
            return false;
        return c[5] is >= (byte)'0' and <= (byte)'9' && c[6] == (byte)'.';
    }

    /// <summary>
    /// ZIP local file header (<c>PK\x03\x04</c>), central directory end
    /// (<c>PK\x05\x06</c>), or spanned-archive marker (<c>PK\x07\x08</c>).
    /// The third and fourth bytes must form one of those three exact pairs
    /// to reject <c>PK\x03\x06</c>-style garbage.
    /// </summary>
    private static bool IsZip(ReadOnlySpan<byte> c)
    {
        if (c.Length < 4) return false;
        if (c[0] != 0x50 || c[1] != 0x4B) return false;
        return (c[2] == 0x03 && c[3] == 0x04)
            || (c[2] == 0x05 && c[3] == 0x06)
            || (c[2] == 0x07 && c[3] == 0x08);
    }

    private static bool IsOle(ReadOnlySpan<byte> c) =>
        c.Length >= 8 &&
        c[0] == 0xD0 && c[1] == 0xCF && c[2] == 0x11 && c[3] == 0xE0 &&
        c[4] == 0xA1 && c[5] == 0xB1 && c[6] == 0x1A && c[7] == 0xE1;

    /// <summary>
    /// RTF: <c>{\rtf</c> followed by a version digit (RTF spec requires
    /// <c>{\rtf1</c> — the <c>1</c> in practice, but the spec allows any
    /// digit for the version field).
    /// </summary>
    private static bool IsRtf(ReadOnlySpan<byte> c)
    {
        if (c.Length < 6) return false;
        if (c[0] != 0x7B || c[1] != 0x5C || c[2] != 0x72 || c[3] != 0x74 || c[4] != 0x66)
            return false;
        return c[5] is >= (byte)'0' and <= (byte)'9';
    }

    private static bool Is7z(ReadOnlySpan<byte> c) =>
        c.Length >= 6 &&
        c[0] == 0x37 && c[1] == 0x7A && c[2] == 0xBC && c[3] == 0xAF &&
        c[4] == 0x27 && c[5] == 0x1C;

    /// <summary>
    /// Gzip: <c>1F 8B</c> plus compression method <c>08</c> (DEFLATE).
    /// RFC 1952 reserves methods 0–7; only 8 is defined. Rejecting
    /// unknown methods blocks crafted gzip polyglots.
    /// </summary>
    private static bool IsGzip(ReadOnlySpan<byte> c) =>
        c.Length >= 3 && c[0] == 0x1F && c[1] == 0x8B && c[2] == 0x08;

    /// <summary>
    /// Bzip2: <c>BZh</c> + block size digit (<c>1</c>–<c>9</c>, hundreds of
    /// kilobytes) + the 6-byte BCD-encoded magic number <c>31 41 59 26 53 59</c>
    /// (the first 6 digits of Pi — the bzip2 compressed-block header).
    /// Validates full 10-byte prefix, not just <c>BZh</c>.
    /// </summary>
    private static bool IsBzip2(ReadOnlySpan<byte> c)
    {
        if (c.Length < 10) return false;
        if (c[0] != 0x42 || c[1] != 0x5A || c[2] != 0x68) return false;
        if (c[3] is < (byte)'1' or > (byte)'9') return false;
        return c[4] == 0x31 && c[5] == 0x41 && c[6] == 0x59
            && c[7] == 0x26 && c[8] == 0x53 && c[9] == 0x59;
    }

    private static bool IsXz(ReadOnlySpan<byte> c) =>
        c.Length >= 6 &&
        c[0] == 0xFD && c[1] == 0x37 && c[2] == 0x7A && c[3] == 0x58 &&
        c[4] == 0x5A && c[5] == 0x00;

    /// <summary>
    /// ISO BMFF: 4-byte box size (≥ 8), <c>ftyp</c> at offset 4, then a
    /// 4-byte major brand which must be printable ASCII. Rejects headers
    /// where the major brand is garbage — a common polyglot failure mode.
    /// </summary>
    private static bool IsFtyp(ReadOnlySpan<byte> c)
    {
        if (c.Length < 12) return false;
        if (c[4] != 0x66 || c[5] != 0x74 || c[6] != 0x79 || c[7] != 0x70) return false;

        // Box size big-endian uint32 at bytes 0-3. Must be ≥ 8 and either
        // representable (≤ content length + some slack) or the special
        // value 0 (box extends to end-of-file) or 1 (64-bit size follows).
        uint boxSize = ((uint)c[0] << 24) | ((uint)c[1] << 16) | ((uint)c[2] << 8) | c[3];
        if (boxSize != 0 && boxSize != 1 && boxSize < 8) return false;

        // Major brand at bytes 8-11 must be printable ASCII.
        for (var i = 8; i < 12; i++)
        {
            var b = c[i];
            if (b < 0x20 || b > 0x7E) return false;
        }
        return true;
    }

    private static bool IsEbml(ReadOnlySpan<byte> c) =>
        c.Length >= 4 &&
        c[0] == 0x1A && c[1] == 0x45 && c[2] == 0xDF && c[3] == 0xA3;

    /// <summary>
    /// Ogg: <c>OggS</c> followed by a version byte that per RFC 3533 §6
    /// must be 0x00. Rejects Ogg-prefixed polyglots with bogus version.
    /// </summary>
    private static bool IsOgg(ReadOnlySpan<byte> c) =>
        c.Length >= 5 &&
        c[0] == 0x4F && c[1] == 0x67 && c[2] == 0x67 && c[3] == 0x53 &&
        c[4] == 0x00;

    /// <summary>
    /// MP3: either an ID3v2 tag (<c>ID3</c> + valid major version 2/3/4)
    /// or a raw MPEG audio frame sync. Uses the strict 12-bit sync
    /// (<c>FF Fx</c>) and validates the layer bits are not reserved.
    /// 12-bit sync is stricter than the 11-bit form — it rejects
    /// MPEG-2.5 (rare/non-standard) and <c>FF E*</c> polyglots — and
    /// implicitly rules out reserved MPEG version 01, since that would
    /// require bit 4 = 0 (top nibble <c>E</c>, not <c>F</c>). Layer bits
    /// (bits 2–1 of byte 1) are still checked: <c>00</c> is reserved
    /// per ISO/IEC 11172-3 and must be rejected.
    /// </summary>
    private static bool IsMp3FrameOrId3(ReadOnlySpan<byte> c)
    {
        if (c.Length >= 4 && c[0] == 0x49 && c[1] == 0x44 && c[2] == 0x33)
        {
            // ID3v2 major version (byte 3) is 2, 3, or 4 in the wild.
            return c[3] is 0x02 or 0x03 or 0x04;
        }

        if (c.Length < 2) return false;
        if (c[0] != 0xFF) return false;
        // Full 12-bit frame sync: top nibble of byte 1 is 0xF.
        if ((c[1] & 0xF0) != 0xF0) return false;
        // Layer bits (bits 2-1): 00=reserved, 01=III, 10=II, 11=I.
        var layer = (c[1] >> 1) & 0x03;
        if (layer == 0x00) return false;
        return true;
    }
}
