// -----------------------------------------------------------------------
// <copyright file="TelegramTextFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.RegularExpressions;

namespace Netclaw.Channels.Telegram;

internal static partial class TelegramTextFormatter
{
    public static string ToHtml(string markdown)
    {
        var html = WebUtility.HtmlEncode(markdown);
        html = FencedCode().Replace(html, "<pre><code>$1</code></pre>");
        html = InlineCode().Replace(html, "<code>$1</code>");
        html = BoldItalic().Replace(html, "<b><i>$1</i></b>");
        html = Bold().Replace(html, "<b>$1</b>");
        html = Italic().Replace(html, "<i>$1</i>");
        html = Link().Replace(html, static match =>
        {
            var encodedUrl = match.Groups[2].Value;
            var decodedUrl = WebUtility.HtmlDecode(encodedUrl);
            return Uri.TryCreate(decodedUrl, UriKind.Absolute, out var url)
                   && url.Scheme is "http" or "https"
                ? $"<a href=\"{encodedUrl}\">{match.Groups[1].Value}</a>"
                : match.Value;
        });
        return html;
    }

    [GeneratedRegex(@"```(?:[^\r\n`]*)\r?\n([\s\S]*?)```", RegexOptions.CultureInvariant)]
    private static partial Regex FencedCode();

    [GeneratedRegex(@"`([^`\r\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*\*(.+?)\*\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex BoldItalic();

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)", RegexOptions.CultureInvariant)]
    private static partial Regex Italic();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\(([^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();
}
