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
        => Format(markdown, richTables: false).Html;

    /// <summary>
    /// Renders markdown with real <c>&lt;table&gt;</c> markup for the rich message API.
    /// Classic <c>ParseMode.Html</c> rejects table tags, so only the rich send path may
    /// use this output.
    /// </summary>
    internal static RichTelegramHtml ToRichHtml(string markdown)
        => Format(markdown, richTables: true);

    private static RichTelegramHtml Format(string markdown, bool richTables)
    {
        if (string.IsNullOrEmpty(markdown))
            return new RichTelegramHtml(string.Empty, ContainsTable: false);

        var output = new StringBuilder(markdown.Length);
        var containsTable = false;
        var position = 0;
        foreach (Match match in FencedCode().Matches(markdown))
        {
            FormatBlocks(markdown[position..match.Index], richTables, output, ref containsTable);
            output.Append("<pre><code>");
            output.Append(WebUtility.HtmlEncode(match.Groups[1].Value));
            output.Append("</code></pre>");
            position = match.Index + match.Length;
        }

        FormatBlocks(markdown[position..], richTables, output, ref containsTable);
        return new RichTelegramHtml(output.ToString(), containsTable);
    }

    private static void FormatBlocks(
        string markdown,
        bool richTables,
        StringBuilder output,
        ref bool containsTable)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (TryFormatTable(lines, ref index, richTables, output, ref containsTable))
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
    }

    private static bool TryFormatTable(
        string[] lines,
        ref int index,
        bool richTables,
        StringBuilder output,
        ref bool containsTable)
    {
        if (index + 2 >= lines.Length)
            return false;

        var headers = ParseTableRow(lines[index]);
        var separators = ParseTableRow(lines[index + 1]);
        if (headers.Count < 2
            || separators.Count != headers.Count
            || separators.Any(separator => !TableSeparator().IsMatch(separator)))
            return false;

        var rows = new List<IReadOnlyList<string>>();
        var rowIndex = index + 2;
        while (rowIndex < lines.Length)
        {
            var values = ParseTableRow(lines[rowIndex]);
            if (values.Count != headers.Count)
                break;
            rows.Add(values);
            rowIndex++;
        }

        if (rows.Count == 0)
            return false;

        index = rowIndex - 1;
        containsTable = true;

        if (!richTables)
        {
            for (var row = 0; row < rows.Count; row++)
            {
                if (row > 0)
                    output.Append('\n');
                for (var column = 0; column < headers.Count; column++)
                {
                    output.Append(column == 0 ? "• " : "  ");
                    output.Append("<b>").Append(FormatInline(headers[column])).Append(":</b> ");
                    output.Append(FormatInline(rows[row][column]));
                    if (column < headers.Count - 1)
                        output.Append('\n');
                }
            }

            return true;
        }

        output.Append("<table bordered><tr>");
        foreach (var header in headers)
            output.Append("<th><b>").Append(FormatInline(header)).Append("</b></th>");
        output.Append("</tr>");
        foreach (var row in rows)
        {
            output.Append("<tr>");
            foreach (var value in row)
                output.Append("<td>").Append(FormatInline(value)).Append("</td>");
            output.Append("</tr>");
        }
        output.Append("</table>");
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

/// <summary>
/// Render output for the rich message API. <see cref="ContainsTable"/> tells the sender
/// whether the HTML carries a native <c>&lt;table&gt;</c> and must not go through the
/// classic <c>ParseMode.Html</c> send path.
/// </summary>
internal sealed record RichTelegramHtml(string Html, bool ContainsTable);
