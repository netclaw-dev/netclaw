// -----------------------------------------------------------------------
// <copyright file="MimeTypeCatalog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Security;

public static class MimeTypeCatalog
{
    public const string ApplicationOctetStream = MimeType.DefaultValue;
    public const string TextPlain = "text/plain";
    public const string TextMarkdown = "text/markdown";
    public const string TextCsv = "text/csv";
    public const string TextHtml = "text/html";
    public const string ApplicationJson = "application/json";
    public const string ApplicationXml = "application/xml";
    public const string ApplicationYaml = "application/yaml";
    public const string ApplicationPdf = "application/pdf";
    public const string ApplicationZip = "application/zip";
    public const string ApplicationOleCompoundDocument = "application/x-ole-compound-document";
    public const string ImagePng = "image/png";
    public const string ImageJpeg = "image/jpeg";
    public const string ImageGif = "image/gif";
    public const string ImageWebp = "image/webp";

    public static string Normalize(string? mimeType)
    {
        var normalized = string.IsNullOrWhiteSpace(mimeType)
            ? ApplicationOctetStream
            : mimeType.Trim();

        var semicolon = normalized.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0)
            normalized = normalized[..semicolon].Trim();

        return string.Equals(normalized, "image/jpg", StringComparison.OrdinalIgnoreCase)
            ? ImageJpeg
            : normalized.ToLowerInvariant();
    }

    public static bool IsText(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        var normalized = Normalize(mimeType);
        if (normalized.StartsWith("text/", StringComparison.Ordinal))
            return true;

        return normalized is ApplicationJson
            or ApplicationXml
            or "application/x-yaml"
            or ApplicationYaml;
    }

    public static bool RequiresBinarySignature(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        return AttachmentCategories.FromMime(Normalize(mimeType)) is
            AttachmentCategory.Image or
            AttachmentCategory.Pdf or
            AttachmentCategory.Document or
            AttachmentCategory.Archive or
            AttachmentCategory.Media;
    }

    public static bool IsZipBackedOfficePath(string path)
    {
        return NormalizeExtension(Path.GetExtension(path)) switch
        {
            ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" => true,
            _ => false
        };
    }

    public static bool IsOleBackedOfficePath(string path)
    {
        return NormalizeExtension(Path.GetExtension(path)) switch
        {
            ".doc" or ".xls" or ".ppt" => true,
            _ => false
        };
    }

    public static string? FromPathExtension(string path) => FromExtension(Path.GetExtension(path));

    public static string? FromExtension(string? extension)
    {
        var ext = NormalizeExtension(extension);
        if (ext.Length == 0)
            return null;

        return ext switch
        {
            ".png" => ImagePng,
            ".jpg" or ".jpeg" => ImageJpeg,
            ".gif" => ImageGif,
            ".webp" => ImageWebp,
            ".svg" => "image/svg+xml",
            ".pdf" => ApplicationPdf,
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/x-m4a",
            ".wav" => "audio/wav",
            ".ogg" or ".oga" => "audio/ogg",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".zip" => ApplicationZip,
            ".gz" or ".tgz" => "application/gzip",
            ".7z" => "application/x-7z-compressed",
            ".bz2" => "application/x-bzip2",
            ".xz" => "application/x-xz",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".odt" => "application/vnd.oasis.opendocument.text",
            ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
            ".odp" => "application/vnd.oasis.opendocument.presentation",
            ".rtf" => "application/rtf",
            ".txt" or ".log" => TextPlain,
            ".md" or ".markdown" => TextMarkdown,
            ".csv" => TextCsv,
            ".html" or ".htm" => TextHtml,
            ".json" => ApplicationJson,
            ".xml" => ApplicationXml,
            ".yml" or ".yaml" => ApplicationYaml,
            _ => null
        };
    }

    public static string ExtensionFor(string? mimeType)
    {
        return Normalize(mimeType) switch
        {
            ImagePng => ".png",
            ImageJpeg => ".jpg",
            ImageGif => ".gif",
            ImageWebp => ".webp",
            "image/svg+xml" => ".svg",
            "audio/mpeg" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/ogg" => ".ogg",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            "video/x-msvideo" => ".avi",
            _ => ".bin"
        };
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var trimmed = extension.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : "." + trimmed.ToLowerInvariant();
    }
}
