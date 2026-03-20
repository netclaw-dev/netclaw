using System.ComponentModel;
using System.Net;
using System.Text;
using HtmlAgilityPack;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Fetches a web page and saves its content to a local file.
/// Default format preserves HTML (links, images, structure); text mode
/// extracts plain text only. Returns a summary with the file path so the
/// agent can use file_read or shell_execute (grep) to selectively read
/// the content without blowing out the context window.
/// </summary>
[NetclawTool("web_fetch",
    "Fetch a web page URL and save its content to a local file. Default format='raw' preserves HTML (links, images, structure); format='text' extracts plain text. Returns file path with preview. Use file_read to examine the full content.",
    Grant = "web")]
public sealed partial class WebFetchTool : NetclawTool<WebFetchTool.Params>
{
    private const int PreviewLines = 10;
    private const int MaxResponseBytes = 5 * 1024 * 1024; // 5MB

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    ];

    private readonly HttpClient _httpClient;
    private readonly string _fetchDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random = new();

    public record Params(
        [property: Description("The URL to fetch")] string Url,
        [property: Description("Output format: 'raw' (default) preserves HTML structure (links, images, tables); 'text' extracts plain text only")]
        string? Format = null);

    public WebFetchTool(HttpClient? httpClient = null, string? fetchDirectory = null, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _fetchDirectory = fetchDirectory
            ?? Path.Combine(Path.GetTempPath(), "netclaw-fetch");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Url))
            return "Error: 'url' parameter is required.";

        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return "Error: Invalid URL. Must be an absolute HTTP or HTTPS URL.";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgents[_random.Next(UserAgents.Length)]);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var content = await ReadWithLimitAsync(response, ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            // Determine format: null or "raw" → raw HTML; "text" → text extraction
            var useTextMode = string.Equals(args.Format, "text", StringComparison.OrdinalIgnoreCase);

            string savedContent;
            string title;
            string extension;
            string previewText;

            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                title = ExtractTitle(content) ?? uri.Host;
                if (useTextMode)
                {
                    savedContent = ExtractTextFromHtml(content);
                    extension = ".txt";
                    previewText = savedContent;
                }
                else
                {
                    savedContent = SanitizeHtml(content);
                    extension = ".html";
                    previewText = ExtractMetadataSummary(content);
                }
            }
            else
            {
                title = uri.Host;
                savedContent = content;
                extension = ".txt";
                previewText = content;
            }

            if (string.IsNullOrWhiteSpace(savedContent))
                return $"Fetched {args.Url} but the page contained no extractable content.";

            // Use session-scoped directory if available, otherwise fall back to shared temp
            var fetchDir = context.SessionDirectory ?? _fetchDirectory;

            // Save to disk
            var filePath = SaveToFile(savedContent, uri, fetchDir, extension);
            var lineCount = savedContent.Count(c => c == '\n') + 1;

            // Build summary with preview
            return FormatSummary(uri.ToString(), title, filePath, savedContent.Length, lineCount, previewText);
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

    private static async Task<string> ReadWithLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Read up to MaxResponseBytes to avoid memory issues
        var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new BinaryReader(stream);
        var bytes = limited.ReadBytes(MaxResponseBytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private string SaveToFile(string content, Uri uri, string directory, string extension = ".txt")
    {
        Directory.CreateDirectory(directory);

        // Generate a filename from the URL host + path + timestamp
        var sanitized = SanitizeForFilename(uri);
        var filename = $"{sanitized}-{_timeProvider.GetUtcNow().ToUnixTimeSeconds()}{extension}";
        var filePath = Path.Combine(directory, filename);

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
