// -----------------------------------------------------------------------
// <copyright file="FilenameSanitizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Security;

/// <summary>
/// Sanitizes filenames to prevent path traversal and other attacks.
/// Adapted from TextForge's AttachmentSecurity.SanitizeFilename().
/// </summary>
public static partial class FilenameSanitizer
{
    /// <summary>
    /// Sanitizes a filename by removing directory components, control characters,
    /// path traversal sequences, and problematic platform characters.
    /// </summary>
    public static string Sanitize(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return "attachment";

        // Normalize path separators for cross-platform handling
        var normalized = filename.Replace('\\', '/');

        // Strip directory components
        var sanitized = Path.GetFileName(normalized);

        // Remove null bytes and control characters
        sanitized = ControlCharRegex().Replace(sanitized, "");

        // Remove characters problematic across platforms
        sanitized = PlatformProblemCharRegex().Replace(sanitized, "_");

        // Remove path traversal sequences
        sanitized = sanitized.Replace("..", "_", StringComparison.Ordinal);

        // Trim whitespace and dots from ends
        sanitized = sanitized.Trim().Trim('.');

        // Limit length preserving extension
        if (sanitized.Length > 255)
        {
            var extension = Path.GetExtension(sanitized);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = nameWithoutExt[..(255 - extension.Length)] + extension;
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "attachment" : sanitized;
    }

    /// <summary>
    /// Checks if a filename contains double extensions that could indicate
    /// a social engineering attack (e.g., "report.exe.png").
    /// </summary>
    public static bool HasSuspiciousDoubleExtension(string filename)
    {
        var remaining = filename;
        var extensionCount = 0;

        while (true)
        {
            var ext = Path.GetExtension(remaining);
            if (string.IsNullOrEmpty(ext))
                break;

            extensionCount++;
            if (extensionCount > 1)
                return true;

            remaining = Path.GetFileNameWithoutExtension(remaining);
        }

        return false;
    }

    [GeneratedRegex(@"[\x00-\x1F\x7F]")]
    private static partial Regex ControlCharRegex();

    [GeneratedRegex(@"[<>:""/\\|?*]")]
    private static partial Regex PlatformProblemCharRegex();
}
