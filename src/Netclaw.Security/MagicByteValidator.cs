using System.Collections.Frozen;

namespace Netclaw.Security;

/// <summary>
/// Validates file content using magic byte (file signature) analysis.
/// Narrowed to image types for Netclaw's multimodal pipeline.
/// </summary>
public static class MagicByteValidator
{
    private static readonly FrozenSet<string> AllowedImageMimeTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/webp"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, FrozenSet<string>> AllowedExtensions =
        new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = new[] { "image/png" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            [".jpg"] = new[] { "image/jpeg" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            [".jpeg"] = new[] { "image/jpeg" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            [".gif"] = new[] { "image/gif" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            [".webp"] = new[] { "image/webp" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Magic byte signatures for executable content — always blocked.
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
    /// Validates file content against its declared MIME type and filename.
    /// Only image types are allowed through.
    /// </summary>
    public static ContentScanResult Validate(
        ReadOnlySpan<byte> content,
        string declaredMimeType,
        string filename,
        ContentPolicy? policy = null)
    {
        if (content.Length == 0)
        {
            return ContentScanResult.Rejected(
                ContentScanError.EmptyContent,
                "File is empty");
        }

        var effectivePolicy = policy ?? new ContentPolicy();
        var allowedMimes = effectivePolicy.AllowedMimeTypes;

        if (content.Length > effectivePolicy.MaxFileSizeBytes)
        {
            return ContentScanResult.Rejected(
                ContentScanError.FileTooLarge,
                $"File exceeds maximum size of {effectivePolicy.MaxFileSizeBytes / (1024 * 1024)} MB");
        }

        // Always check for executables — these are never allowed
        if (HasExecutableSignature(content))
        {
            var detectedType = DetectMimeType(content);
            return ContentScanResult.Rejected(
                ContentScanError.ExecutableContent,
                "Executable content detected",
                detectedType is not null ? new MimeType(detectedType) : null);
        }

        // Validate extension is in allowlist
        var extension = Path.GetExtension(filename);
        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.ContainsKey(extension))
        {
            return ContentScanResult.Rejected(
                ContentScanError.UnrecognizedFileType,
                $"File extension '{extension}' is not allowed");
        }

        // Validate declared MIME type is allowed
        if (!allowedMimes.Contains(declaredMimeType))
        {
            return ContentScanResult.Rejected(
                ContentScanError.UnrecognizedFileType,
                $"MIME type '{declaredMimeType}' is not allowed");
        }

        // Validate extension matches declared MIME type
        var allowedMimesForExtension = AllowedExtensions[extension];
        if (!allowedMimesForExtension.Contains(declaredMimeType))
        {
            return ContentScanResult.Rejected(
                ContentScanError.MimeTypeMismatch,
                $"Extension '{extension}' does not match declared type '{declaredMimeType}'");
        }

        // Validate magic bytes match expected image type
        return ValidateImageContent(content, declaredMimeType);
    }

    /// <summary>
    /// Detects MIME type from magic bytes for the supported image types.
    /// Returns null for unrecognized content.
    /// </summary>
    public static string? DetectMimeType(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 8 &&
            content[0] == 0x89 && content[1] == 0x50 &&
            content[2] == 0x4E && content[3] == 0x47 &&
            content[4] == 0x0D && content[5] == 0x0A &&
            content[6] == 0x1A && content[7] == 0x0A)
            return "image/png";

        if (content.Length >= 3 &&
            content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
            return "image/jpeg";

        if (content.Length >= 4 &&
            content[0] == 0x47 && content[1] == 0x49 &&
            content[2] == 0x46 && content[3] == 0x38)
            return "image/gif";

        if (content.Length >= 12 &&
            content[0] == 0x52 && content[1] == 0x49 &&
            content[2] == 0x46 && content[3] == 0x46 &&
            content[8] == 0x57 && content[9] == 0x45 &&
            content[10] == 0x42 && content[11] == 0x50)
            return "image/webp";

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

    private static ContentScanResult ValidateImageContent(
        ReadOnlySpan<byte> content,
        string declaredMimeType)
    {
        var detectedMimeType = DetectMimeType(content);

        if (declaredMimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
        {
            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (content.Length < 8 ||
                content[0] != 0x89 || content[1] != 0x50 ||
                content[2] != 0x4E || content[3] != 0x47 ||
                content[4] != 0x0D || content[5] != 0x0A ||
                content[6] != 0x1A || content[7] != 0x0A)
            {
                return ContentScanResult.Rejected(
                    ContentScanError.MimeTypeMismatch,
                    "Content is not a valid PNG file",
                    detectedMimeType is not null ? new MimeType(detectedMimeType) : null);
            }
        }
        else if (declaredMimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            // JPEG: FF D8 FF
            if (content.Length < 3 ||
                content[0] != 0xFF || content[1] != 0xD8 || content[2] != 0xFF)
            {
                return ContentScanResult.Rejected(
                    ContentScanError.MimeTypeMismatch,
                    "Content is not a valid JPEG file",
                    detectedMimeType is not null ? new MimeType(detectedMimeType) : null);
            }
        }
        else if (declaredMimeType.Equals("image/gif", StringComparison.OrdinalIgnoreCase))
        {
            // GIF: GIF8 (47 49 46 38)
            if (content.Length < 4 ||
                content[0] != 0x47 || content[1] != 0x49 ||
                content[2] != 0x46 || content[3] != 0x38)
            {
                return ContentScanResult.Rejected(
                    ContentScanError.MimeTypeMismatch,
                    "Content is not a valid GIF file",
                    detectedMimeType is not null ? new MimeType(detectedMimeType) : null);
            }
        }
        else if (declaredMimeType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
        {
            // WebP: RIFF....WEBP (52 49 46 46 xx xx xx xx 57 45 42 50)
            if (content.Length < 12 ||
                content[0] != 0x52 || content[1] != 0x49 ||
                content[2] != 0x46 || content[3] != 0x46 ||
                content[8] != 0x57 || content[9] != 0x45 ||
                content[10] != 0x42 || content[11] != 0x50)
            {
                return ContentScanResult.Rejected(
                    ContentScanError.MimeTypeMismatch,
                    "Content is not a valid WebP file",
                    detectedMimeType is not null ? new MimeType(detectedMimeType) : null);
            }
        }

        return ContentScanResult.Allowed(
            new MimeType(detectedMimeType ?? declaredMimeType));
    }
}
