// -----------------------------------------------------------------------
// <copyright file="MimeTypeCatalog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Frozen;

namespace Netclaw.Media;

public static class MimeTypeCatalog
{
    public const string ApplicationOctetStream = MimeType.DefaultValue;
    public const string TextPlain = "text/plain";
    public const string TextMarkdown = "text/markdown";
    public const string TextCsv = "text/csv";
    public const string TextTsv = "text/tab-separated-values";
    public const string TextHtml = "text/html";
    public const string ApplicationJson = "application/json";
    public const string ApplicationXml = "application/xml";
    public const string ApplicationYaml = "application/yaml";
    public const string ApplicationPdf = "application/pdf";
    public const string ApplicationZip = "application/zip";
    public const string ApplicationOleCompoundDocument = "application/x-ole-compound-document";
    public const string ApplicationRtf = "application/rtf";
    public const string ImagePng = "image/png";
    public const string ImageJpeg = "image/jpeg";
    public const string ImageGif = "image/gif";
    public const string ImageWebp = "image/webp";
    public const string ImageBmp = "image/bmp";
    public const string ImageTiff = "image/tiff";
    public const string AudioMpeg = "audio/mpeg";
    public const string AudioMp4 = "audio/mp4";
    public const string AudioWav = "audio/wav";
    public const string AudioOgg = "audio/ogg";
    public const string VideoMp4 = "video/mp4";
    public const string VideoQuickTime = "video/quicktime";
    public const string VideoWebm = "video/webm";
    public const string VideoMatroska = "video/x-matroska";
    public const string VideoAvi = "video/x-msvideo";

    private static readonly FrozenDictionary<string, string> AliasesByMime = BuildAliases()
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly MediaTypeDefinition[] Definitions = BuildDefinitions();

    private static readonly FrozenDictionary<string, MediaTypeDefinition> DefinitionsByMime = Definitions
        .ToFrozenDictionary(static d => d.MimeType.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, MediaTypeDefinition> DefinitionsByExtension = BuildExtensionMap()
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<(string Extension, string DeclaredMime), string> ExtensionMimeOverrides =
        BuildExtensionMimeOverrides().ToFrozenDictionary(new ExtensionMimePairComparer());

    public static IReadOnlyCollection<MediaTypeDefinition> All => Definitions;

    public static string Normalize(string? mimeType)
    {
        var normalized = string.IsNullOrWhiteSpace(mimeType)
            ? ApplicationOctetStream
            : mimeType.Trim();

        var semicolon = normalized.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0)
            normalized = normalized[..semicolon].Trim();

        normalized = normalized.ToLowerInvariant();
        return AliasesByMime.TryGetValue(normalized, out var canonical)
            ? canonical
            : normalized;
    }

    public static MimeType NormalizeDeclaredForExtension(string? declaredMimeType, string? extension)
    {
        var canonical = new MimeType(declaredMimeType);
        var ext = new FileExtension(extension);
        if (ext.IsEmpty)
            return canonical;

        if (ExtensionMimeOverrides.TryGetValue((ext.Value, canonical.Value), out var corrected))
            return new MimeType(corrected);

        if (canonical.Value == ApplicationOctetStream && TryGetFromExtension(ext, out var extensionMime))
            return extensionMime;

        return canonical;
    }

    public static bool TryGet(MimeType mimeType, out MediaTypeDefinition definition) =>
        DefinitionsByMime.TryGetValue(mimeType.Value, out definition!);

    public static AttachmentCategory GetCategory(string? mimeType) => GetCategory(new MimeType(mimeType));

    public static AttachmentCategory GetCategory(MimeType mimeType) =>
        TryGet(mimeType, out var definition) ? definition.Category : AttachmentCategory.Other;

    public static MediaKind GetMediaKind(string? mimeType) => GetMediaKind(new MimeType(mimeType));

    public static MediaKind GetMediaKind(MimeType mimeType) =>
        TryGet(mimeType, out var definition) ? definition.MediaKind : MediaKind.Unknown;

    public static MediaContentKind GetContentKind(string? mimeType) => GetContentKind(new MimeType(mimeType));

    public static MediaContentKind GetContentKind(MimeType mimeType) =>
        TryGet(mimeType, out var definition) ? definition.ContentKind : MediaContentKind.Unknown;

    public static bool IsText(string? mimeType) => IsText(new MimeType(mimeType));

    public static bool IsText(MimeType mimeType) =>
        TryGet(mimeType, out var definition) && definition.ContentKind == MediaContentKind.Text;

    public static bool RequiresBinarySignature(string? mimeType) => RequiresBinarySignature(new MimeType(mimeType));

    public static bool RequiresBinarySignature(MimeType mimeType) =>
        TryGet(mimeType, out var definition)
        && definition.SupportsNativeSignatureValidation
        && definition.ContentKind == MediaContentKind.Binary;

    public static bool SupportsNativeSignatureValidation(string? mimeType) =>
        SupportsNativeSignatureValidation(new MimeType(mimeType));

    public static bool SupportsNativeSignatureValidation(MimeType mimeType) =>
        TryGet(mimeType, out var definition) && definition.SupportsNativeSignatureValidation;

    public static IEnumerable<string> GetNativeSignatureValidatedMimeTypes() => Definitions
        .Where(static d => d.SupportsNativeSignatureValidation)
        .Select(static d => d.MimeType.Value);

    public static bool IsModelInputSupported(string? mimeType) => IsModelInputSupported(new MimeType(mimeType));

    public static bool IsModelInputSupported(MimeType mimeType) =>
        TryGet(mimeType, out var definition) && definition.SupportsModelInput;

    public static bool TryGetFromPathExtension(string path, out MimeType mimeType) =>
        TryGetFromExtension(FileExtension.FromPath(path), out mimeType);

    public static MimeType? FromPathExtension(string path) =>
        TryGetFromPathExtension(path, out var mimeType) ? mimeType : null;

    public static MimeType? FromExtension(string? extension) =>
        TryGetFromExtension(new FileExtension(extension), out var mimeType) ? mimeType : null;

    public static bool TryGetFromExtension(FileExtension extension, out MimeType mimeType)
    {
        if (!extension.IsEmpty && DefinitionsByExtension.TryGetValue(extension.Value, out var definition))
        {
            mimeType = definition.MimeType;
            return true;
        }

        mimeType = default;
        return false;
    }

    public static string ExtensionFor(string? mimeType) => ExtensionFor(new MimeType(mimeType));

    public static string ExtensionFor(MimeType mimeType) =>
        TryGet(mimeType, out var definition) ? definition.DefaultExtension.Value : ".bin";

    public static bool ExtensionMatches(MimeType mimeType, string? extension)
    {
        if (!TryGet(mimeType, out var definition))
            return false;

        var ext = new FileExtension(extension);
        return !ext.IsEmpty && definition.Extensions.Any(e => e.Value.Equals(ext.Value, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsZipBackedOfficePath(string path)
    {
        return FileExtension.FromPath(path).Value switch
        {
            ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" => true,
            _ => false
        };
    }

    public static bool IsOleBackedOfficePath(string path)
    {
        return FileExtension.FromPath(path).Value switch
        {
            ".doc" or ".xls" or ".ppt" => true,
            _ => false
        };
    }

    private static Dictionary<string, string> BuildAliases() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpg"] = ImageJpeg,
        ["application/x-zip-compressed"] = ApplicationZip,
        ["application/x-gzip"] = "application/gzip",
        ["application/x-yaml"] = ApplicationYaml,
        ["text/xml"] = ApplicationXml,
        ["text/rtf"] = ApplicationRtf,
        ["audio/x-m4a"] = AudioMp4,
        ["audio/x-wav"] = AudioWav
    };

    private static MediaTypeDefinition[] BuildDefinitions() =>
    [
        Binary(ImagePng, AttachmentCategory.Image, MediaKind.Image, true, true, ".png", ".png"),
        Binary(ImageJpeg, AttachmentCategory.Image, MediaKind.Image, true, true, ".jpg", ".jpg", ".jpeg"),
        Binary(ImageGif, AttachmentCategory.Image, MediaKind.Image, true, true, ".gif", ".gif"),
        Binary(ImageWebp, AttachmentCategory.Image, MediaKind.Image, true, true, ".webp", ".webp"),
        // bmp/tiff are accepted as images but not model-input-eligible: providers
        // ingest png/jpeg/gif/webp, so these stay path-only (see AttachmentInlineDecision).
        Binary(ImageBmp, AttachmentCategory.Image, MediaKind.Image, true, false, ".bmp", ".bmp"),
        Binary(ImageTiff, AttachmentCategory.Image, MediaKind.Image, true, false, ".tiff", ".tif", ".tiff"),

        Binary(ApplicationPdf, AttachmentCategory.Pdf, MediaKind.Pdf, true, false, ".pdf", ".pdf"),

        Binary("application/vnd.openxmlformats-officedocument.wordprocessingml.document", AttachmentCategory.Document, MediaKind.Document, true, false, ".docx", ".docx"),
        Binary("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", AttachmentCategory.Document, MediaKind.Document, true, false, ".xlsx", ".xlsx"),
        Binary("application/vnd.openxmlformats-officedocument.presentationml.presentation", AttachmentCategory.Document, MediaKind.Document, true, false, ".pptx", ".pptx"),
        Binary("application/vnd.oasis.opendocument.text", AttachmentCategory.Document, MediaKind.Document, true, false, ".odt", ".odt"),
        Binary("application/vnd.oasis.opendocument.spreadsheet", AttachmentCategory.Document, MediaKind.Document, true, false, ".ods", ".ods"),
        Binary("application/vnd.oasis.opendocument.presentation", AttachmentCategory.Document, MediaKind.Document, true, false, ".odp", ".odp"),
        Binary("application/msword", AttachmentCategory.Document, MediaKind.Document, true, false, ".doc", ".doc"),
        Binary("application/vnd.ms-excel", AttachmentCategory.Document, MediaKind.Document, true, false, ".xls", ".xls"),
        Binary("application/vnd.ms-powerpoint", AttachmentCategory.Document, MediaKind.Document, true, false, ".ppt", ".ppt"),
        Binary(ApplicationRtf, AttachmentCategory.Document, MediaKind.Document, true, false, ".rtf", ".rtf"),

        Text(TextPlain, ".txt", ".txt", ".log"),
        Text(TextMarkdown, ".md", ".md", ".markdown"),
        Text(TextCsv, ".csv", ".csv"),
        Text(TextTsv, ".tsv", ".tsv"),
        Text(TextHtml, ".html", ".html", ".htm"),
        Text(ApplicationJson, ".json", ".json"),
        Text(ApplicationXml, ".xml", ".xml"),
        Text(ApplicationYaml, ".yaml", ".yml", ".yaml"),

        Binary(ApplicationZip, AttachmentCategory.Archive, MediaKind.Archive, true, false, ".zip", ".zip"),
        Binary("application/x-7z-compressed", AttachmentCategory.Archive, MediaKind.Archive, true, false, ".7z", ".7z"),
        Binary("application/gzip", AttachmentCategory.Archive, MediaKind.Archive, true, false, ".gz", ".gz", ".tgz"),
        Binary("application/x-bzip2", AttachmentCategory.Archive, MediaKind.Archive, true, false, ".bz2", ".bz2"),
        Binary("application/x-xz", AttachmentCategory.Archive, MediaKind.Archive, true, false, ".xz", ".xz"),

        Binary(AudioMpeg, AttachmentCategory.Media, MediaKind.Audio, true, false, ".mp3", ".mp3"),
        Binary(AudioMp4, AttachmentCategory.Media, MediaKind.Audio, true, false, ".m4a", ".m4a", ".mp4"),
        Binary(AudioWav, AttachmentCategory.Media, MediaKind.Audio, true, false, ".wav", ".wav"),
        Binary(AudioOgg, AttachmentCategory.Media, MediaKind.Audio, true, false, ".ogg", ".ogg", ".oga"),
        Binary(VideoMp4, AttachmentCategory.Media, MediaKind.Video, true, false, ".mp4", ".mp4", ".m4v"),
        Binary(VideoQuickTime, AttachmentCategory.Media, MediaKind.Video, true, false, ".mov", ".mov"),
        Binary(VideoWebm, AttachmentCategory.Media, MediaKind.Video, true, false, ".webm", ".webm"),
        Binary(VideoMatroska, AttachmentCategory.Media, MediaKind.Video, true, false, ".mkv", ".mkv"),
        Binary(VideoAvi, AttachmentCategory.Media, MediaKind.Video, true, false, ".avi", ".avi")
    ];

    private static MediaTypeDefinition Text(string mimeType, string defaultExtension, params string[] extensions) =>
        new(mimeType, AttachmentCategory.Document, MediaKind.Text, MediaContentKind.Text, true, false, defaultExtension, extensions);

    private static MediaTypeDefinition Binary(
        string mimeType,
        AttachmentCategory category,
        MediaKind mediaKind,
        bool supportsNativeSignatureValidation,
        bool supportsModelInput,
        string defaultExtension,
        params string[] extensions) =>
        new(mimeType, category, mediaKind, MediaContentKind.Binary, supportsNativeSignatureValidation, supportsModelInput, defaultExtension, extensions);

    private static Dictionary<string, MediaTypeDefinition> BuildExtensionMap()
    {
        var map = new Dictionary<string, MediaTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            foreach (var extension in definition.Extensions)
                map.TryAdd(extension.Value, definition);
        }

        map[".mp4"] = DefinitionsByCanonical(VideoMp4);
        return map;
    }

    private static MediaTypeDefinition DefinitionsByCanonical(string mimeType) =>
        Definitions.Single(d => d.MimeType.Value.Equals(mimeType, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<(string Extension, string DeclaredMime), string> BuildExtensionMimeOverrides() => new(new ExtensionMimePairComparer())
    {
        [(".md", TextPlain)] = TextMarkdown,
        [(".markdown", TextPlain)] = TextMarkdown,
        [(".json", TextPlain)] = ApplicationJson,
        [(".yaml", TextPlain)] = ApplicationYaml,
        [(".yml", TextPlain)] = ApplicationYaml,
        [(".csv", TextPlain)] = TextCsv,
        [(".tsv", TextPlain)] = TextTsv,
        [(".xml", TextPlain)] = ApplicationXml,
        [(".html", TextPlain)] = TextHtml,
        [(".htm", TextPlain)] = TextHtml
    };

    private sealed class ExtensionMimePairComparer : IEqualityComparer<(string Extension, string DeclaredMime)>
    {
        public bool Equals((string Extension, string DeclaredMime) x, (string Extension, string DeclaredMime) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Extension, y.Extension)
            && StringComparer.OrdinalIgnoreCase.Equals(x.DeclaredMime, y.DeclaredMime);

        public int GetHashCode((string Extension, string DeclaredMime) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Extension),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.DeclaredMime));
    }
}
