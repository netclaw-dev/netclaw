// -----------------------------------------------------------------------
// <copyright file="WebFetchTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers;
using System.ComponentModel;
using System.Net;
using System.Text;
using HtmlAgilityPack;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Fetches a URL and saves its content to a local file.
/// For HTML: default format preserves structure; text mode extracts plain text.
/// For binary content (images, PDFs, etc.): saves raw bytes with correct extension.
/// For other text content: saves as-is with the URL's file extension preserved.
/// Returns a summary with the file path so the agent can use file_read,
/// grep, or attach_file to work with the content.
/// </summary>
[NetclawTool("web_fetch",
    "Use for retrieving a known external page or URL. Save content to a local file without shell. " +
    "HTML raw mode preserves structure; text mode extracts text. Binary content keeps its correct extension. " +
    "Returns a file path with preview. Use file_read to examine content or attach_file to send binary files.",
    Grant = "web")]
public sealed partial class WebFetchTool : NetclawTool<WebFetchTool.Params>
{
    private const int PreviewLines = 10;
    private const int MaxResponseBytes = 5 * 1024 * 1024; // 5MB

    private const string TruncationNotice =
        "\n[content truncated at 5 MB — the response exceeded the cap; the remainder was not fetched]";

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    ];

    private readonly WebFetchConfig _webFetchConfig;
    private readonly HttpClient _httpClient;
    private readonly string _fetchDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random = new();

    public record Params(
        [property: Description("The URL to fetch")] string Url,
        [property: Description("Output format: 'raw' (default) preserves HTML structure (links, images, tables); 'text' extracts plain text only")]
        string? Format = null);

    public WebFetchTool(ToolConfig config, HttpClient? httpClient = null, string? fetchDirectory = null, TimeProvider? timeProvider = null)
    {
        _webFetchConfig = config.WebFetch;
        _httpClient = httpClient ?? new HttpClient();
        _fetchDirectory = fetchDirectory
            ?? Path.Combine(Path.GetTempPath(), "netclaw-fetch");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Test convenience constructor — uses default config.
    /// </summary>
    public WebFetchTool(HttpClient? httpClient = null, string? fetchDirectory = null, TimeProvider? timeProvider = null)
        : this(new ToolConfig(), httpClient, fetchDirectory, timeProvider) { }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Url))
            return "Error: 'url' parameter is required.";

        // An unsupported format must not silently fall back to raw mode — the
        // agent asked for an output shape we don't produce (netclaw-tools spec:
        // web fetch format is validated).
        if (args.Format is not null
            && !string.Equals(args.Format, "raw", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(args.Format, "text", StringComparison.OrdinalIgnoreCase))
        {
            return $"Error: Parameter 'Format' value '{args.Format}' is not supported. "
                 + "Valid values: raw, text. The fetch was NOT performed.";
        }

        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return "Error: Invalid URL. Must be an absolute HTTP or HTTPS URL.";

        if (_webFetchConfig.RequireHttps
            && uri.Scheme == "http"
            && !_webFetchConfig.HttpAllowList.Any(h => h.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Error: HTTPS is required by security policy. The URL '{args.Url}' uses plain HTTP. "
                 + "Use https:// instead, or ask the operator to add the host to Tools.WebFetch.HttpAllowList "
                 + "or set Tools.WebFetch.RequireHttps to false in the configuration.";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgents[_random.Next(UserAgents.Length)]);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var fetchDir = context.SessionDirectory ?? _fetchDirectory;

            if (IsBinaryContentType(contentType))
            {
                var (bytes, binaryTruncated) = await ReadBytesWithLimitAsync(response, ct);
                if (bytes.Length == 0)
                    return $"Fetched {args.Url} but the response was empty.";

                var extension = GetExtensionFromUrl(uri)
                    ?? GetFallbackExtension(contentType, isBinary: true);
                var filePath = SaveBytesToFile(bytes, uri, fetchDir, extension);

                var binarySummary = FormatBinarySummary(uri.ToString(), filePath, bytes.Length, contentType);
                return binaryTruncated ? binarySummary + TruncationNotice : binarySummary;
            }

            var (content, contentTruncated) = await ReadTextWithLimitAsync(response, ct);
            var useTextMode = string.Equals(args.Format, "text", StringComparison.OrdinalIgnoreCase);

            string savedContent;
            string title;
            string textExtension;
            string previewText;

            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                title = ExtractTitle(content) ?? uri.Host;
                if (useTextMode)
                {
                    savedContent = ExtractTextFromHtml(content);
                    textExtension = ".txt";
                    previewText = savedContent;
                }
                else
                {
                    savedContent = SanitizeHtml(content);
                    textExtension = ".html";
                    previewText = ExtractMetadataSummary(content);
                }
            }
            else
            {
                title = uri.Host;
                savedContent = content;
                textExtension = GetExtensionFromUrl(uri)
                    ?? GetFallbackExtension(contentType, isBinary: false);
                previewText = content;
            }

            if (string.IsNullOrWhiteSpace(savedContent))
                return $"Fetched {args.Url} but the page contained no extractable content.";

            var textFilePath = SaveToFile(savedContent, uri, fetchDir, textExtension);
            var lineCount = savedContent.Count(c => c == '\n') + 1;

            var summary = FormatSummary(uri.ToString(), title, textFilePath, savedContent.Length, lineCount, previewText);
            return contentTruncated ? summary + TruncationNotice : summary;
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Failed to fetch URL: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out.";
        }
    }

    internal static bool IsBinaryContentType(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        var mimeType = new MimeType(contentType);
        if (MimeTypeCatalog.GetContentKind(mimeType) == MediaContentKind.Binary
            || mimeType.Value == MimeTypeCatalog.ApplicationOctetStream)
            return true;

        // Media families are inherently binary even when the exact subtype is
        // not in the catalog (e.g. image/avif, image/heic, video/x-flv); never
        // UTF-8-decode those as text. mimeType.Value is already normalized.
        return mimeType.Value.StartsWith("image/", StringComparison.Ordinal)
            || mimeType.Value.StartsWith("audio/", StringComparison.Ordinal)
            || mimeType.Value.StartsWith("video/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Extract file extension from the URL path (e.g., /photo.png → .png).
    /// Returns null if the URL has no recognizable extension.
    /// Filters out numeric-only "extensions" like .25414 (version numbers, IDs).
    /// </summary>
    internal static string? GetExtensionFromUrl(Uri uri)
    {
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrEmpty(ext))
            return null;

        // Filter out numeric-only extensions (e.g., .25414 from arxiv URLs)
        if (ext.Length > 1 && ext.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
            return null;

        return ext;
    }

    /// <summary>
    /// Fallback extension when the URL has no file extension.
    /// Covers common Content-Types; defaults to .bin (binary) or .txt (text).
    /// </summary>
    internal static string GetFallbackExtension(string contentType, bool isBinary)
    {
        var extension = MimeTypeCatalog.ExtensionFor(contentType);
        return extension != ".bin"
            ? extension
            : isBinary ? ".bin" : ".txt";
    }

    private static async Task<(string Content, bool Truncated)> ReadTextWithLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var (bytes, truncated) = await ReadBytesWithLimitAsync(response, ct);
        return (Encoding.UTF8.GetString(bytes), truncated);
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadBytesWithLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct);

        // Stream into a buffer that grows to the actual content size, capped at
        // MaxResponseBytes. Avoids eagerly allocating the full cap for every
        // small fetch (the old BinaryReader.ReadBytes(cap) cost) and the 5 MB
        // slice-copy a truncated response used to pay. Truncation is detected by
        // reading one extra byte past the cap, so it is surfaced, not inferred.
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                var remaining = MaxResponseBytes - (int)buffer.Length;
                if (read >= remaining)
                {
                    buffer.Write(chunk, 0, remaining);
                    return (buffer.ToArray(), Truncated: true);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return (buffer.ToArray(), Truncated: false);
    }

    private string BuildFilePath(Uri uri, string directory, string extension)
    {
        Directory.CreateDirectory(directory);
        var sanitized = SanitizeForFilename(uri);

        // The timestamp is for a human to read and sort, not for uniqueness.
        // Two fetches of the same URL within one second gave the same
        // filename, and File.WriteAllBytes/WriteAllText overwrote the first
        // fetch with no warning. The guid segment gives real uniqueness;
        // millisecond precision keeps same-second fetches in fetch order
        // when the directory is sorted by name.
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var filename = $"{sanitized}-{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}-{uniqueSuffix}{extension}";
        return Path.Combine(directory, filename);
    }

    private string SaveBytesToFile(byte[] content, Uri uri, string directory, string extension)
    {
        var filePath = BuildFilePath(uri, directory, extension);
        File.WriteAllBytes(filePath, content);
        return filePath;
    }

    private string SaveToFile(string content, Uri uri, string directory, string extension)
    {
        var filePath = BuildFilePath(uri, directory, extension);
        File.WriteAllText(filePath, content, Encoding.UTF8);
        return filePath;
    }

    private static string FormatSummary(
        string url, string title, string filePath, int charCount, int lineCount, string text)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Fetched: {url}");
        sb.AppendLine($"Title: {title}");
        sb.AppendLine($"Saved to: {filePath} ({charCount:N0} chars, {lineCount:N0} lines)");
        sb.AppendLine();
        sb.AppendLine("Preview (first lines):");

        var lines = text.Split('\n');
        var previewCount = Math.Min(PreviewLines, lines.Length);
        for (var i = 0; i < previewCount; i++)
        {
            var line = lines[i].TrimEnd();
            if (!string.IsNullOrEmpty(line))
                sb.AppendLine(line);
        }

        if (lines.Length > PreviewLines)
            sb.AppendLine($"... ({lines.Length - PreviewLines} more lines — use file_read or grep to search)");

        return sb.ToString().TrimEnd();
    }

    private static string FormatBinarySummary(
        string url, string filePath, int byteCount, string contentType)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Fetched: {url}");
        sb.AppendLine($"Saved to: {filePath} ({byteCount:N0} bytes)");
        sb.AppendLine($"Content-Type: {contentType}");
        sb.AppendLine();
        sb.AppendLine("This is a binary file. Use attach_file to send it to the user, or file_read if the format supports text extraction.");
        return sb.ToString().TrimEnd();
    }

    internal static string SanitizeForFilename(Uri uri)
    {
        var raw = $"{uri.Host}{uri.AbsolutePath}";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
        }

        // Trim and limit length
        var result = sb.ToString().Trim('_');
        return result.Length > 60 ? result[..60] : result;
    }

    /// <summary>
    /// Extract the page title from HTML.
    /// </summary>
    internal static string? ExtractTitle(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode is null)
            return null;
        var title = WebUtility.HtmlDecode(titleNode.InnerText).Trim();
        return string.IsNullOrEmpty(title) ? null : title;
    }

    /// <summary>
    /// Remove script and style elements from HTML but preserve all other structure.
    /// Used in raw mode to reduce noise while keeping links, images, and layout.
    /// </summary>
    internal static string SanitizeHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style");
        if (nodesToRemove is not null)
        {
            foreach (var node in nodesToRemove)
                node.Remove();
        }

        return doc.DocumentNode.OuterHtml;
    }

    /// <summary>
    /// Extract a brief metadata summary for raw-mode preview.
    /// Shows meta description and top headings instead of raw HTML.
    /// </summary>
    internal static string ExtractMetadataSummary(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var sb = new StringBuilder();

        var metaDesc = doc.DocumentNode.SelectSingleNode("//meta[@name='description']");
        if (metaDesc?.GetAttributeValue("content", "") is { Length: > 0 } desc)
            sb.AppendLine($"Description: {WebUtility.HtmlDecode(desc)}");

        var headings = doc.DocumentNode.SelectNodes("//h1|//h2|//h3");
        if (headings is { Count: > 0 })
        {
            var shown = 0;
            foreach (var h in headings)
            {
                var text = WebUtility.HtmlDecode(h.InnerText).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    sb.AppendLine($"  {h.Name.ToUpperInvariant()}: {text}");
                    if (++shown >= 5) break;
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Extract readable text from HTML by removing scripts, styles, and tags.
    /// Preserves basic structure through line breaks.
    /// </summary>
    internal static string ExtractTextFromHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove script and style elements entirely
        var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//noscript|//svg|//nav|//footer|//header");
        if (nodesToRemove is not null)
        {
            foreach (var node in nodesToRemove)
                node.Remove();
        }

        var sb = new StringBuilder();
        ExtractText(doc.DocumentNode, sb);

        // Clean up excessive whitespace while preserving paragraph breaks
        var lines = sb.ToString()
            .Split('\n')
            .Select(l => l.Trim())
            .ToArray();

        var result = new StringBuilder();
        var lastWasEmpty = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                if (!lastWasEmpty)
                {
                    result.AppendLine();
                    lastWasEmpty = true;
                }
                continue;
            }

            result.AppendLine(line);
            lastWasEmpty = false;
        }

        return result.ToString().Trim();
    }

    private static void ExtractText(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = WebUtility.HtmlDecode(node.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
                sb.Append(text.Trim()).Append(' ');
            return;
        }

        // Block elements get line breaks
        var isBlock = node.Name is "p" or "div" or "br" or "h1" or "h2" or "h3"
            or "h4" or "h5" or "h6" or "li" or "tr" or "blockquote" or "pre"
            or "article" or "section" or "main";

        if (isBlock && sb.Length > 0)
            sb.AppendLine();

        foreach (var child in node.ChildNodes)
            ExtractText(child, sb);

        if (isBlock)
            sb.AppendLine();
    }
}
