// -----------------------------------------------------------------------
// <copyright file="TelegramTextFormatter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Netclaw.Channels.Telegram;

internal static partial class TelegramTextFormatter
{
    public static string ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var output = new StringBuilder(markdown.Length);
        var position = 0;
        foreach (Match match in FencedCode().Matches(markdown))
        {
            output.Append(FormatBlocks(markdown[position..match.Index]));
            output.Append("<pre><code>");
            output.Append(WebUtility.HtmlEncode(match.Groups[1].Value));
            output.Append("</code></pre>");
            position = match.Index + match.Length;
        }

        output.Append(FormatBlocks(markdown[position..]));
        return output.ToString();
    }

    private static string FormatBlocks(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder(markdown.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var heading = Heading().Match(line);
            var quote = BlockQuote().Match(line);
            var bullet = BulletListItem().Match(line);

            if (heading.Success)
                output.Append("<b>").Append(FormatInline(heading.Groups[1].Value)).Append("</b>");
            else if (quote.Success)
                output.Append("<blockquote>").Append(FormatInline(quote.Groups[1].Value)).Append("</blockquote>");
            else if (bullet.Success)
                output.Append("• ").Append(FormatInline(bullet.Groups[1].Value));
            else
                output.Append(FormatInline(line));

            if (index < lines.Length - 1)
                output.Append('\n');
        }

        return output.ToString();
    }

    private static string FormatInline(string markdown)
    {
        var output = new StringBuilder(markdown.Length);
        var position = 0;
        foreach (Match match in InlineCode().Matches(markdown))
        {
            output.Append(FormatInlineMarkup(markdown[position..match.Index]));
            output.Append("<code>");
            output.Append(WebUtility.HtmlEncode(match.Groups[1].Value));
            output.Append("</code>");
            position = match.Index + match.Length;
        }

        output.Append(FormatInlineMarkup(markdown[position..]));
        return output.ToString();
    }

    private static string FormatInlineMarkup(string markdown)
    {
        var html = WebUtility.HtmlEncode(markdown);
        html = BoldItalic().Replace(html, "<b><i>$1</i></b>");
        html = Bold().Replace(html, "<b>$1</b>");
        html = Italic().Replace(html, "<i>$1</i>");
        html = Strikethrough().Replace(html, "<s>$1</s>");
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

    [GeneratedRegex(@"~~([^~\r\n]+)~~", RegexOptions.CultureInvariant)]
    private static partial Regex Strikethrough();

    [GeneratedRegex(@"^\s*#{1,6}\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*>\s?(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockQuote();

    [GeneratedRegex(@"^\s*[-*+]\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex BulletListItem();
}
