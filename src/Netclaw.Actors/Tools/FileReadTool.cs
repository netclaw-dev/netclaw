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
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Reads text files and inspects non-text files without returning raw bytes.
/// </summary>
[NetclawTool(ToolName,
    "Read text files or inspect non-text files. Images can be loaded for visual inspection when the active model supports image input; PDFs/media/archives return metadata and guidance. For large text files, use StartLine and Limit to read sections.",
    Grant = "file")]
public sealed partial class FileReadTool : NetclawTool<FileReadTool.Params>
{
    public const string ToolName = "file_read";
    private const long MaxModelInputFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32Le = new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);
    private static readonly Encoding Windows1252 = new Windows1252Encoding();

    private readonly ToolConfig _config;
    private readonly ToolPathPolicy? _pathPolicy;
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;
    private readonly SkillRegistry? _skillRegistry;
    private readonly ISessionMetrics? _sessionMetrics;
    private readonly ILogger? _logger;

    public record Params(
        [property: Description("Absolute path to the file to read")] string Path,
        [property: Description("Line number to start reading at, 1-based: the first line is line 1, matching the line numbers shown in this tool's output and in editors/grep/sed. To read line N, pass StartLine=N. Use with Limit to read sections of large files and avoid context window truncation.")] int? StartLine = null,
        [property: Description("Maximum number of lines to read. Use with StartLine to paginate through large files instead of reading the whole file.")] int? Limit = null);

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
        int? startLine = args.StartLine > 0 ? args.StartLine : null;
        int? limit = args.Limit > 0 ? args.Limit : null;

        try
        {
            var inspection = await InspectFileAsync(authorizedPath, ct);
            if (!inspection.IsTextLike)
                return HandleNonTextFile(authorizedPath, inspection, context);

            var encoding = inspection.TextEncoding ?? StrictUtf8;
            if (startLine.HasValue || limit.HasValue)
            {
                var lines = await ReadLinesAsync(authorizedPath, encoding, startLine ?? 1, limit, _config.MaxOutputChars, ct);
                RecordSkillReadIfApplicable(authorizedPath);
                return lines; // redaction + inline bound + spill happen centrally in the dispatcher
            }

            // Bounded head read (not ReadAllTextAsync): a multi-hundred-MB file must
            // not be materialized just to truncate it — read up to the limit and stop.
            // Closes #1301. Redaction and the inline-budget bound + spill happen
            // centrally in DispatchingToolExecutor; file_read only bounds its read for
            // memory safety and steers past the cap.
            var (content, truncated) = await ReadBoundedHeadAsync(authorizedPath, encoding, _config.MaxOutputChars, ct);
            RecordSkillReadIfApplicable(authorizedPath);
            return truncated
                ? content + $"\n[output truncated at {_config.MaxOutputChars} chars — read a specific range with StartLine and Limit, or grep the file, for the rest]"
                : content;
        }
        catch (DecoderFallbackException)
        {
            var sizeBytes = TryGetFileLength(authorizedPath);
            return BuildMetadataResponse(
                authorizedPath,
                new FileInspection(MimeType.Default, AttachmentCategory.Other, sizeBytes, false, null),
                "File is not valid in the detected text encoding. Raw binary output is not returned by file_read.");
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
            return new FileInspection(new MimeType(MimeTypeCatalog.TextPlain), AttachmentCategory.Document, 0, true, StrictUtf8);

        var sampleLength = (int)Math.Min(info.Length, 4096);
        var buffer = new byte[sampleLength];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, sampleLength), ct);
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);
        }

        var magicMime = MagicByteValidator.DetectMimeType(buffer.AsSpan(0, Math.Min(buffer.Length, 64)));
        var extensionMime = MimeTypeCatalog.FromPathExtension(path);
        var textEncoding = DetectTextEncoding(buffer, extensionMime);
        var looksText = textEncoding is not null;
        var mimeType = ResolveMimeType(path, magicMime, extensionMime, textEncoding);
        var category = MimeTypeCatalog.GetCategory(mimeType);
        var isTextLike = looksText && MimeTypeCatalog.IsText(mimeType);

        return new FileInspection(mimeType, category, info.Length, isTextLike, isTextLike ? textEncoding : null);
    }

    private static MimeType ResolveMimeType(
        string path,
        string? magicMime,
        MimeType? extensionMime,
        Encoding? textEncoding)
    {
        var looksText = textEncoding is not null;
        // ZIP/OLE Office containers should be explained as documents, not as
        // generic archives or OLE blobs. The extension is only trusted after the
        // container signature proves this is the expected binary family.
        if (MimeTypeCatalog.IsZipBackedOfficePath(path)
            && string.Equals(magicMime, MimeTypeCatalog.ApplicationZip, StringComparison.OrdinalIgnoreCase))
            return extensionMime!.Value;

        if (MimeTypeCatalog.IsOleBackedOfficePath(path)
            && string.Equals(magicMime, MimeTypeCatalog.ApplicationOleCompoundDocument, StringComparison.OrdinalIgnoreCase))
            return extensionMime!.Value;

        if (looksText && extensionMime is not null && MimeTypeCatalog.IsText(extensionMime.Value) && IsUtf16OrUtf32(textEncoding))
            return extensionMime.Value;

        if (magicMime is not null)
            return new MimeType(magicMime);

        if (looksText)
            return extensionMime is not null && MimeTypeCatalog.IsText(extensionMime.Value)
                ? extensionMime.Value
                : new MimeType(MimeTypeCatalog.TextPlain);

        // Extensions are only hints. A binary file named `.json` or `.png`
        // must stay metadata-only instead of leaking control bytes or spoofed
        // media into a tool result / model input path.
        if (extensionMime is not null && MimeTypeCatalog.IsText(extensionMime.Value))
            return MimeType.Default;

        if (extensionMime is not null && MimeTypeCatalog.RequiresBinarySignature(extensionMime.Value))
            return MimeType.Default;

        return extensionMime ?? MimeType.Default;
    }

    private string HandleNonTextFile(
        string authorizedPath,
        FileInspection inspection,
        ToolExecutionContext context)
    {
        var inlineImages = context.ModelInputModalities.HasFlag(ModelModality.Image);
        var (inlined, note) = AttachmentInlineDecision.Resolve(inspection.MimeType, inspection.Category, inlineImages);

        if (inspection.Category == AttachmentCategory.Image && inlined)
        {
            if (inspection.SizeBytes > MaxModelInputFileBytes)
            {
                return BuildMetadataResponse(
                    authorizedPath,
                    inspection,
                    $"Image exceeds the {ByteSizeFormatter.Format(MaxModelInputFileBytes)} model-input handoff limit. Raw binary output is not returned by file_read.");
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
               $"Size: {ByteSizeFormatter.Format(inspection.SizeBytes)}\n" +
               guidance;
    }

    private static Encoding? DetectTextEncoding(ReadOnlySpan<byte> sample, MimeType? extensionMime)
    {
        if (sample.Length == 0)
            return StrictUtf8;

        if (TryDetectBomEncoding(sample, out var bomEncoding, out var bomLength))
            return DecodedTextLooksLikeText(sample[bomLength..], bomEncoding) ? bomEncoding : null;

        if (TryDetectUtf16WithoutBom(sample, out var utf16Encoding)
            && DecodedTextLooksLikeText(sample, utf16Encoding))
        {
            return utf16Encoding;
        }

        if (RawTextControlsAreAcceptable(sample) && CanDecodeUtf8Sample(sample))
            return StrictUtf8;

        return extensionMime is not null
               && MimeTypeCatalog.IsText(extensionMime.Value)
               && RawTextControlsAreAcceptable(sample)
               && DecodedTextLooksLikeText(sample, Windows1252)
            ? Windows1252
            : null;
    }

    private static bool TryDetectBomEncoding(
        ReadOnlySpan<byte> sample,
        out Encoding encoding,
        out int bomLength)
    {
        if (sample.Length >= 4)
        {
            if (sample[0] == 0xFF && sample[1] == 0xFE && sample[2] == 0x00 && sample[3] == 0x00)
            {
                encoding = StrictUtf32Le;
                bomLength = 4;
                return true;
            }

            if (sample[0] == 0x00 && sample[1] == 0x00 && sample[2] == 0xFE && sample[3] == 0xFF)
            {
                encoding = StrictUtf32Be;
                bomLength = 4;
                return true;
            }
        }

        if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
        {
            encoding = StrictUtf8;
            bomLength = 3;
            return true;
        }

        if (sample.Length >= 2)
        {
            if (sample[0] == 0xFF && sample[1] == 0xFE)
            {
                encoding = StrictUtf16Le;
                bomLength = 2;
                return true;
            }

            if (sample[0] == 0xFE && sample[1] == 0xFF)
            {
                encoding = StrictUtf16Be;
                bomLength = 2;
                return true;
            }
        }

        encoding = StrictUtf8;
        bomLength = 0;
        return false;
    }

    private static bool TryDetectUtf16WithoutBom(ReadOnlySpan<byte> sample, out Encoding encoding)
    {
        var pairs = Math.Min(sample.Length / 2, 256);
        if (pairs < 4)
        {
            encoding = StrictUtf8;
            return false;
        }

        var evenNul = 0;
        var oddNul = 0;
        for (var i = 0; i < pairs * 2; i += 2)
        {
            if (sample[i] == 0)
                evenNul++;
            if (sample[i + 1] == 0)
                oddNul++;
        }

        if (oddNul >= pairs / 2 && evenNul <= Math.Max(1, pairs / 20))
        {
            encoding = StrictUtf16Le;
            return true;
        }

        if (evenNul >= pairs / 2 && oddNul <= Math.Max(1, pairs / 20))
        {
            encoding = StrictUtf16Be;
            return true;
        }

        encoding = StrictUtf8;
        return false;
    }

    private static bool CanDecodeUtf8Sample(ReadOnlySpan<byte> sample)
    {
        var decoder = StrictUtf8.GetDecoder();
        var bytes = sample.ToArray();
        var chars = new char[StrictUtf8.GetMaxCharCount(bytes.Length)];
        try
        {
            decoder.Convert(bytes, 0, bytes.Length, chars, 0, chars.Length, flush: false, out _, out _, out _);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool DecodedTextLooksLikeText(ReadOnlySpan<byte> sample, Encoding encoding)
    {
        var decoder = encoding.GetDecoder();
        var bytes = sample.ToArray();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        try
        {
            decoder.Convert(bytes, 0, bytes.Length, chars, 0, chars.Length, flush: false, out _, out var charsUsed, out _);
            return TextControlsAreAcceptable(chars.AsSpan(0, charsUsed));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool RawTextControlsAreAcceptable(ReadOnlySpan<byte> sample)
    {
        if (sample.Length == 0)
            return true;

        var controlCount = 0;
        foreach (var b in sample)
        {
            if (b == 0)
                return false;

            if (b < 0x20 && b is not ((byte)'\n') and not ((byte)'\r') and not ((byte)'\t') and not 0x1B)
                controlCount++;
        }

        return controlCount <= Math.Max(1, sample.Length / 20);
    }

    private static bool TextControlsAreAcceptable(ReadOnlySpan<char> sample)
    {
        if (sample.Length == 0)
            return true;

        var controlCount = 0;
        foreach (var c in sample)
        {
            if (c == '\0')
                return false;

            if (char.IsControl(c) && c is not '\n' and not '\r' and not '\t' and not '\u001B')
                controlCount++;
        }

        return controlCount <= Math.Max(1, sample.Length / 20);
    }

    private static bool IsUtf16OrUtf32(Encoding? encoding)
    {
        return ReferenceEquals(encoding, StrictUtf16Le)
               || ReferenceEquals(encoding, StrictUtf16Be)
               || ReferenceEquals(encoding, StrictUtf32Le)
               || ReferenceEquals(encoding, StrictUtf32Be);
    }

    /// <summary>
    /// Reads up to <paramref name="maxChars"/> chars from the head of the file and
    /// stops — bounding both memory and I/O so a huge file is never fully
    /// materialized. Returns the content and whether more remained past the cap.
    /// </summary>
    private static async Task<(string Content, bool Truncated)> ReadBoundedHeadAsync(
        string path, Encoding encoding, int maxChars, CancellationToken ct)
    {
        using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
        var sb = new StringBuilder();
        var buf = new char[4096];
        while (sb.Length < maxChars)
        {
            var want = Math.Min(buf.Length, maxChars - sb.Length);
            var read = await reader.ReadAsync(buf.AsMemory(0, want), ct);
            if (read == 0)
                return (sb.ToString(), false); // EOF before the cap — not truncated
            sb.Append(buf, 0, read);
        }

        // Hit the cap: truncated iff at least one more char remains on disk.
        return (sb.ToString(), reader.Peek() >= 0);
    }

    private static async Task<string> ReadLinesAsync(
        string path,
        Encoding encoding,
        int startLine,
        int? maxLines,
        int maxChars,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var lineNumber = 0;
        var linesRead = 0;

        using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
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
                return sb.ToString(0, maxChars) + $"\n[output truncated at line {lineNumber} — use StartLine={lineNumber} with Limit to continue reading]";
        }

        return sb.ToString();
    }

    private sealed record FileInspection(
        MimeType MimeType,
        AttachmentCategory Category,
        long SizeBytes,
        bool IsTextLike,
        Encoding? TextEncoding);

    private static long TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed class Windows1252Encoding : Encoding
    {
        private static readonly char[] C1Map =
        [
            '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
            '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
            '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
            '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178'
        ];

        public override string WebName => "windows-1252";

        public override int GetByteCount(char[] chars, int index, int count) => count;

        public override int GetBytes(
            char[] chars,
            int charIndex,
            int charCount,
            byte[] bytes,
            int byteIndex)
        {
            for (var i = 0; i < charCount; i++)
                bytes[byteIndex + i] = EncodeChar(chars[charIndex + i]);

            return charCount;
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        public override int GetChars(
            byte[] bytes,
            int byteIndex,
            int byteCount,
            char[] chars,
            int charIndex)
        {
            for (var i = 0; i < byteCount; i++)
                chars[charIndex + i] = DecodeByte(bytes[byteIndex + i]);

            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) => charCount;

        public override int GetMaxCharCount(int byteCount) => byteCount;

        private static char DecodeByte(byte b)
        {
            return b is >= 0x80 and <= 0x9F
                ? C1Map[b - 0x80]
                : (char)b;
        }

        private static byte EncodeChar(char c)
        {
            if (c <= 0x7F || c is >= '\u00A0' and <= '\u00FF')
                return (byte)c;

            for (var i = 0; i < C1Map.Length; i++)
            {
                if (C1Map[i] == c)
                    return (byte)(0x80 + i);
            }

            return (byte)'?';
        }
    }

    private void RecordSkillReadIfApplicable(string authorizedPath)
    {
        var skill = _skillRegistry?.GetByFilePath(authorizedPath);
        if (skill is null)
            return;

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.FileRead);
        _logger?.LogInformation("turn_skill_loaded skill={SkillName} method=file_read", skill.Name);
    }
}
