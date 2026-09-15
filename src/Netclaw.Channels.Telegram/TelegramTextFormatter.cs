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
            if (TryFormatTable(lines, ref index, output))
            {
                if (index < lines.Length - 1)
                    output.Append('\n');
                continue;
            }

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

    private static bool TryFormatTable(string[] lines, ref int index, StringBuilder output)
    {
        if (index + 2 >= lines.Length)
            return false;

        var headers = ParseTableRow(lines[index]);
        var separators = ParseTableRow(lines[index + 1]);
        if (headers.Count < 2
            || separators.Count != headers.Count
            || separators.Any(separator => !TableSeparator().IsMatch(separator)))
            return false;

        var rowIndex = index + 2;
        var wroteRow = false;
        while (rowIndex < lines.Length)
        {
            var values = ParseTableRow(lines[rowIndex]);
            if (values.Count != headers.Count)
                break;

            if (wroteRow)
                output.Append('\n');

            for (var column = 0; column < headers.Count; column++)
            {
                output.Append(column == 0 ? "• " : "  ");
                output.Append("<b>").Append(FormatInline(headers[column])).Append(":</b> ");
                output.Append(FormatInline(values[column]));
                if (column < headers.Count - 1)
                    output.Append('\n');
            }

            wroteRow = true;
            rowIndex++;
        }

        if (!wroteRow)
            return false;

        index = rowIndex - 1;
        return true;
    }

    private static IReadOnlyList<string> ParseTableRow(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.Contains('|', StringComparison.Ordinal))
            return [];

        if (trimmed.StartsWith('|'))
            trimmed = trimmed[1..];
        if (trimmed.EndsWith('|'))
            trimmed = trimmed[..^1];

        return trimmed.Split('|').Select(cell => cell.Trim()).ToArray();
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
        var output = new StringBuilder(markdown.Length);
        var position = 0;
        while (position < markdown.Length)
        {
            var match = FindNextInlineMatch(markdown, position);
            if (match is null)
            {
                output.Append(WebUtility.HtmlEncode(markdown[position..]));
                break;
            }

            output.Append(WebUtility.HtmlEncode(markdown[position..match.Index]));
            output.Append(match.Html);
            position = match.Index + match.Length;
        }

        return output.ToString();
    }

    private static InlineMatch? FindNextInlineMatch(string markdown, int position)
    {
        InlineMatch? best = null;
        Consider(BoldItalic(), match => $"<b><i>{WebUtility.HtmlEncode(match.Groups[1].Value)}</i></b>");
        Consider(Bold(), match => $"<b>{WebUtility.HtmlEncode(match.Groups[1].Value)}</b>");
        Consider(Italic(), match => $"<i>{WebUtility.HtmlEncode(match.Groups[1].Value)}</i>");
        Consider(Strikethrough(), match => $"<s>{WebUtility.HtmlEncode(match.Groups[1].Value)}</s>");
        Consider(Link(), FormatLink);
        return best;

        void Consider(Regex regex, Func<Match, string> format)
        {
            var match = regex.Match(markdown, position);
            if (!match.Success || best is not null && match.Index >= best.Index)
                return;

            best = new InlineMatch(match.Index, match.Length, format(match));
        }
    }

    private static string FormatLink(Match match)
    {
        var urlText = match.Groups[2].Value;
        return Uri.TryCreate(urlText, UriKind.Absolute, out var url)
               && url.Scheme is "http" or "https"
            ? $"<a href=\"{WebUtility.HtmlEncode(urlText)}\">{WebUtility.HtmlEncode(match.Groups[1].Value)}</a>"
            : WebUtility.HtmlEncode(match.Value);
    }

    private sealed record InlineMatch(int Index, int Length, string Html);

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

    [GeneratedRegex(@"^:?-{3,}:?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableSeparator();
}
