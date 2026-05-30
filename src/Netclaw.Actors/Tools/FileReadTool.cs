// -----------------------------------------------------------------------
// <copyright file="FileReadTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Telemetry;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Reads UTF-8 text files and inspects non-text files without returning raw bytes.
/// </summary>
[NetclawTool(ToolName,
    "Read text files or inspect non-text files. Images can be loaded for visual inspection when the active model supports image input; PDFs/media/archives return metadata and guidance. For large text files, use Offset and Limit to read sections.",
    Grant = "file")]
public sealed partial class FileReadTool : NetclawTool<FileReadTool.Params>
{
    public const string ToolName = "file_read";
    private const long MaxModelInputFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ToolConfig _config;
    private readonly ToolPathPolicy? _pathPolicy;
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;
    private readonly SkillRegistry? _skillRegistry;
    private readonly ISessionMetrics? _sessionMetrics;
    private readonly ILogger? _logger;

    public record Params(
        [property: Description("Absolute path to the file to read")] string Path,
        [property: Description("Line number to start reading from (1-based). Use with Limit to read sections of large files and avoid context window truncation.")] int? Offset = null,
        [property: Description("Maximum number of lines to read. Use with Offset to paginate through large files instead of reading the whole file.")] int? Limit = null);

    public FileReadTool(
        ToolConfig config,
        ToolPathPolicy? pathPolicy = null,
        NetclawPaths? paths = null,
        SkillRegistry? skillRegistry = null,
        ISessionMetrics? sessionMetrics = null,
        ILogger<FileReadTool>? logger = null)
    {
        _config = config;
        _pathPolicy = pathPolicy;
        _fileAccessPolicy = new ScopedFileAccessPolicy(config, paths);
        _skillRegistry = skillRegistry;
        _sessionMetrics = sessionMetrics;
        _logger = logger;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return "Error: 'path' parameter is required.";

        if (!_fileAccessPolicy.TryResolveReadPath(args.Path, context, out var authorizedPath, out var accessError))
            return accessError;

        if (_pathPolicy?.IsReadDenied(authorizedPath) == true)
            return FileToolErrors.CredentialReadDenied(authorizedPath);

        if (!File.Exists(authorizedPath))
            return $"Error: File not found: {authorizedPath}";

        // Treat 0 or negative as "not specified"
        int? offset = args.Offset > 0 ? args.Offset : null;
        int? limit = args.Limit > 0 ? args.Limit : null;

        try
        {
            var inspection = await InspectFileAsync(authorizedPath, ct);
            if (!inspection.IsTextLike)
                return HandleNonTextFile(authorizedPath, inspection, context);

            if (offset.HasValue || limit.HasValue)
            {
                var lines = await ReadLinesAsync(authorizedPath, offset ?? 1, limit, _config.MaxOutputChars, ct);
                RecordSkillReadIfApplicable(authorizedPath);
                return lines;
            }

            var content = await File.ReadAllTextAsync(authorizedPath, StrictUtf8, ct);
            RecordSkillReadIfApplicable(authorizedPath);
            return TruncateFileOutput(content, _config.MaxOutputChars);
        }
        catch (DecoderFallbackException)
        {
            return BuildMetadataResponse(
                authorizedPath,
                new FileInspection("application/octet-stream", AttachmentCategory.Other, new FileInfo(authorizedPath).Length, false),
                "File is not valid UTF-8 text. Raw binary output is not returned by file_read.");
        }
        catch (UnauthorizedAccessException)
        {
            return $"Error: Permission denied: {authorizedPath}";
        }
        catch (IOException ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    private static async Task<FileInspection> InspectFileAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length == 0)
            return new FileInspection("text/plain", AttachmentCategory.Document, 0, true);

        var sampleLength = (int)Math.Min(info.Length, 4096);
        var buffer = new byte[sampleLength];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, sampleLength), ct);
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);
        }

        var magicMime = MagicByteValidator.DetectMimeType(buffer.AsSpan(0, Math.Min(buffer.Length, 64)));
        var extensionMime = GuessMimeType(path);
        var looksText = LooksLikeText(buffer);
        var mimeType = ResolveMimeType(path, magicMime, extensionMime, looksText);
        var category = AttachmentCategories.FromMime(mimeType);
        var isTextLike = looksText && IsTextMime(mimeType);

        return new FileInspection(mimeType, category, info.Length, isTextLike);
    }

    private static string ResolveMimeType(
        string path,
        string? magicMime,
        string? extensionMime,
        bool looksText)
    {
        // ZIP/OLE Office containers should be explained as documents, not as
        // generic archives or OLE blobs. The extension is only trusted after the
        // container signature proves this is the expected binary family.
        if (IsZipBackedOfficeDocument(path) && string.Equals(magicMime, "application/zip", StringComparison.OrdinalIgnoreCase))
            return extensionMime!;

        if (IsOleBackedOfficeDocument(path)
            && string.Equals(magicMime, "application/x-ole-compound-document", StringComparison.OrdinalIgnoreCase))
            return extensionMime!;

        if (magicMime is not null)
            return magicMime;

        if (looksText)
            return IsTextMime(extensionMime) ? extensionMime! : "text/plain";

        // Extensions are only hints. A binary file named `.json` or `.png`
        // must stay metadata-only instead of leaking control bytes or spoofed
        // media into a tool result / model input path.
        if (IsTextMime(extensionMime))
            return "application/octet-stream";

        if (RequiresBinarySignature(extensionMime))
            return "application/octet-stream";

        return extensionMime ?? "application/octet-stream";
    }

    private string HandleNonTextFile(
        string authorizedPath,
        FileInspection inspection,
        ToolExecutionContext context)
    {
        var inlineImages = context.ModelInputModalities.HasFlag(ModelModality.Image);
        var (inlined, note) = AttachmentInlineDecision.Resolve(inspection.Category, inlineImages);

        if (inspection.Category == AttachmentCategory.Image && inlined)
        {
            if (inspection.SizeBytes > MaxModelInputFileBytes)
            {
                return BuildMetadataResponse(
                    authorizedPath,
                    inspection,
                    $"Image exceeds the {FormatBytes(MaxModelInputFileBytes)} model-input handoff limit. Raw binary output is not returned by file_read.");
            }

            context.AddModelInputFile(authorizedPath, Path.GetFileName(authorizedPath), inspection.MimeType);
            return BuildMetadataResponse(
                authorizedPath,
                inspection,
                "Image loaded for model-visible inspection on the next LLM call.");
        }

        var guidance = inspection.Category switch
        {
            AttachmentCategory.Image => note ?? AttachmentNotes.ModelMissingImage,
            AttachmentCategory.Pdf => "Native PDF extraction is not built into file_read. Use a configured document processor or shell_execute with a tool such as pdftotext where available.",
            AttachmentCategory.Media => "Audio transcription and video keyframe extraction are not built into file_read. Use a configured media processor where available.",
            AttachmentCategory.Archive => "Archive extraction is not built into file_read. Use a configured archive processor or shell_execute where available.",
            AttachmentCategory.Document => "Binary document extraction is not built into file_read. Use a configured document processor where available.",
            _ => "File is not readable as UTF-8 text. Raw binary output is not returned by file_read."
        };

        return BuildMetadataResponse(authorizedPath, inspection, guidance);
    }

    private static string BuildMetadataResponse(
        string path,
        FileInspection inspection,
        string guidance)
    {
        return $"File is not readable as plain text.\n" +
               $"Path: {path}\n" +
               $"Type: {inspection.MimeType} ({inspection.Category})\n" +
               $"Size: {FormatBytes(inspection.SizeBytes)}\n" +
               guidance;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> sample)
    {
        if (sample.Length == 0)
            return true;

        var controlCount = 0;
        foreach (var b in sample)
        {
            if (b == 0)
                return false;

            if (b < 0x20 && b is not ((byte)'\n') and not ((byte)'\r') and not ((byte)'\t'))
                controlCount++;
        }

        if (controlCount > Math.Max(1, sample.Length / 20))
            return false;

        try
        {
            StrictUtf8.GetString(sample);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsTextMime(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        return mimeType.ToLowerInvariant() switch
        {
            "application/json" => true,
            "application/xml" => true,
            "application/x-yaml" => true,
            "application/yaml" => true,
            _ => false
        };
    }

    private static bool RequiresBinarySignature(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        return AttachmentCategories.FromMime(mimeType) is
            AttachmentCategory.Image or
            AttachmentCategory.Pdf or
            AttachmentCategory.Document or
            AttachmentCategory.Archive or
            AttachmentCategory.Media;
    }

    private static bool IsZipBackedOfficeDocument(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" => true,
            _ => false
        };
    }

    private static bool IsOleBackedOfficeDocument(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".doc" or ".xls" or ".ppt" => true,
            _ => false
        };
    }

    private static string? GuessMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".zip" => "application/zip",
            ".gz" => "application/gzip",
            ".7z" => "application/x-7z-compressed",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".rtf" => "application/rtf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".yml" or ".yaml" => "application/yaml",
            _ => null
        };
    }

    private static string FormatBytes(long size) => size switch
    {
        >= 1024L * 1024L => $"{size / (1024d * 1024d):F1} MiB",
        >= 1024L => $"{size / 1024d:F1} KiB",
        _ => $"{size} bytes"
    };

    private static string TruncateFileOutput(string content, int maxChars)
    {
        if (content.Length <= maxChars)
            return content;
        int newlinesBefore = 0, totalNewlines = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            totalNewlines++;
            if (i < maxChars) newlinesBefore++;
        }
        var nextLine = newlinesBefore + 1;
        var totalLines = totalNewlines + 1;
        return string.Concat(content.AsSpan(0, maxChars),
            $"\n[output truncated at line {nextLine} of {totalLines} — use Offset={nextLine} with Limit to continue reading]");
    }

    private static async Task<string> ReadLinesAsync(
        string path, int startLine, int? maxLines, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var lineNumber = 0;
        var linesRead = 0;

        using var reader = new StreamReader(path, StrictUtf8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (lineNumber < startLine)
                continue;

            if (maxLines.HasValue && linesRead >= maxLines.Value)
                break;

            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append($"{lineNumber,6}\t{line}");
            linesRead++;

            if (sb.Length >= maxChars)
                return sb.ToString(0, maxChars) + $"\n[output truncated at line {lineNumber} — use Offset={lineNumber} with Limit to continue reading]";
        }

        return sb.ToString();
    }

    private sealed record FileInspection(
        string MimeType,
        AttachmentCategory Category,
        long SizeBytes,
        bool IsTextLike);

    private void RecordSkillReadIfApplicable(string authorizedPath)
    {
        var skill = _skillRegistry?.GetByFilePath(authorizedPath);
        if (skill is null)
            return;

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.FileRead);
        _logger?.LogInformation("turn_skill_loaded skill={SkillName} method=file_read", skill.Name);
    }
}
