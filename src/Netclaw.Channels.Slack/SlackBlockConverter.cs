// -----------------------------------------------------------------------
// <copyright file="SlackBlockConverter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using SlackNet.Blocks;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Converts standard markdown (as emitted by LLMs) into Slack Block Kit
/// rich text blocks for proper formatting in Slack messages.
/// </summary>
public static partial class SlackBlockConverter
{
    /// <summary>
    /// Convert markdown text to a list of Slack Block Kit blocks.
    /// </summary>
    public static List<Block> Convert(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var blocks = new List<Block>();
        var lines = markdown.Split('\n');
        var currentRichTextElements = new List<RichTextElement>();
        var i = 0;
        var tableRendered = false;

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Blank line — paragraph separator
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                i++;
                continue;
            }

            if (!tableRendered && TryParseMarkdownTable(lines, i, out var table, out var tableLineCount))
            {
                FlushRichText(blocks, currentRichTextElements);
                blocks.Add(table);
                tableRendered = true;
                i += tableLineCount;
                continue;
            }

            // Headers: # / ## / ### → HeaderBlock
            if (trimmed.StartsWith('#'))
            {
                FlushRichText(blocks, currentRichTextElements);
                var headerText = trimmed.TrimStart('#').Trim();
                if (!string.IsNullOrEmpty(headerText))
                {
                    // Strip inline markdown from header text (bold markers etc.)
                    headerText = StripInlineMarkdown(headerText);
                    blocks.Add(new HeaderBlock { Text = new PlainText { Text = headerText } });
                }
                i++;
                continue;
            }

            // Code block: ```
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushRichText(blocks, currentRichTextElements);
                i++; // skip opening ```
                var codeLines = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // skip closing ```

                var codeText = string.Join("\n", codeLines);
                currentRichTextElements.Add(new RichTextPreformatted
                {
                    Elements = { new RichTextText { Text = codeText } }
                });
                FlushRichText(blocks, currentRichTextElements);
                continue;
            }

            // Blockquote: > text
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushRichText(blocks, currentRichTextElements);
                var quoteText = trimmed[2..];
                currentRichTextElements.Add(new RichTextQuote
                {
                    Elements = ParseInlineElements(quoteText)
                });
                FlushRichText(blocks, currentRichTextElements);
                i++;
                continue;
            }

            // Bullet list: - item or * item (not bold **)
            if (IsBulletListItem(trimmed))
            {
                FlushRichText(blocks, currentRichTextElements);
                var listItems = new List<RichTextSection>();
                while (i < lines.Length && IsBulletListItem(lines[i].TrimStart()))
                {
                    var itemText = StripBulletPrefix(lines[i].TrimStart());
                    listItems.Add(new RichTextSection
                    {
                        Elements = ParseInlineElements(itemText)
                    });
                    i++;
                }
                currentRichTextElements.Add(new RichTextList
                {
                    Style = RichTextListStyle.Bullet,
                    Elements = listItems
                });
                FlushRichText(blocks, currentRichTextElements);
                continue;
            }

            // Ordered list: 1. item
            if (IsOrderedListItem(trimmed))
            {
                FlushRichText(blocks, currentRichTextElements);
                var listItems = new List<RichTextSection>();
                while (i < lines.Length && IsOrderedListItem(lines[i].TrimStart()))
                {
                    var itemText = StripOrderedPrefix(lines[i].TrimStart());
                    listItems.Add(new RichTextSection
                    {
                        Elements = ParseInlineElements(itemText)
                    });
                    i++;
                }
                currentRichTextElements.Add(new RichTextList
                {
                    Style = RichTextListStyle.Ordered,
                    Elements = listItems
                });
                FlushRichText(blocks, currentRichTextElements);
                continue;
            }

            // Regular paragraph text
            currentRichTextElements.Add(new RichTextSection
            {
                Elements = ParseInlineElements(trimmed)
            });
            i++;
        }

        FlushRichText(blocks, currentRichTextElements);
        return blocks;
    }

    private static bool TryParseMarkdownTable(
        IReadOnlyList<string> lines,
        int startIndex,
        out TableBlock table,
        out int lineCount)
    {
        table = null!;
        lineCount = 0;

        if (startIndex + 1 >= lines.Count
            || !TryParseTableRow(lines[startIndex], out var headerCells)
            || headerCells.Count is 0 or > MaxTableColumns
            || !IsTableDivider(lines[startIndex + 1], headerCells.Count))
        {
            return false;
        }

        var rows = new List<IList<TableCell>>
        {
            ToTableCells(headerCells)
        };
        var characterCount = headerCells.Sum(cell => cell.Length);
        var nextIndex = startIndex + 2;

        while (nextIndex < lines.Count && TryParseTableRow(lines[nextIndex], out var cells))
        {
            if (cells.Count != headerCells.Count
                || rows.Count == MaxTableRows
                || characterCount + cells.Sum(cell => cell.Length) > MaxTableCharacters)
            {
                return false;
            }

            rows.Add(ToTableCells(cells));
            characterCount += cells.Sum(cell => cell.Length);
            nextIndex++;
        }

        table = new TableBlock { Rows = rows };
        lineCount = nextIndex - startIndex;
        return true;
    }

    private static bool IsTableDivider(string line, int columnCount)
    {
        return TryParseTableRow(line, out var cells)
            && cells.Count == columnCount
            && cells.All(cell => TableDividerCellRegex().IsMatch(cell));
    }

    private static bool TryParseTableRow(string line, out List<string> cells)
    {
        var trimmed = line.Trim();
        cells = [];

        if (!trimmed.Contains('|', StringComparison.Ordinal))
            return false;

        var startIndex = trimmed.StartsWith('|') ? 1 : 0;
        var endIndex = trimmed.EndsWith('|') ? trimmed.Length - 1 : trimmed.Length;
        var currentCell = new System.Text.StringBuilder();

        for (var index = startIndex; index < endIndex; index++)
        {
            var character = trimmed[index];
            if (character == '\\' && index + 1 < endIndex && trimmed[index + 1] == '|')
            {
                currentCell.Append('|');
                index++;
                continue;
            }

            if (character == '|')
            {
                cells.Add(currentCell.ToString().Trim());
                currentCell.Clear();
                continue;
            }

            currentCell.Append(character);
        }

        cells.Add(currentCell.ToString().Trim());
        return cells.Count > 0;
    }

    private static IList<TableCell> ToTableCells(IEnumerable<string> cells)
    {
        return cells
            .Select(ToTableCell)
            .ToList();
    }

    private static TableCell ToTableCell(string cell)
    {
        var elements = ParseInlineElements(cell);
        if (!elements.Any(RequiresRichTextCell))
            return new RawTextCell { Text = cell };

        return new RichTextCell
        {
            Elements =
            [
                new RichTextSection { Elements = elements }
            ]
        };
    }

    private static bool RequiresRichTextCell(RichTextSectionElement element)
    {
        return element is not RichTextText
        {
            Style:
            {
                Bold: false,
                Italic: false,
                Strike: false,
                Code: false,
                Highlight: false,
                ClientHighlight: false,
                Underline: false,
                Unlink: false
            }
        };
    }

    private static void FlushRichText(List<Block> blocks, List<RichTextElement> elements)
    {
        if (elements.Count == 0) return;

        blocks.Add(new RichTextBlock
        {
            Elements = new List<RichTextElement>(elements)
        });
        elements.Clear();
    }

    /// <summary>
    /// Parse inline markdown (bold, italic, code, links, strikethrough)
    /// into a list of <see cref="RichTextSectionElement"/>.
    /// </summary>
    internal static List<RichTextSectionElement> ParseInlineElements(string text)
    {
        var elements = new List<RichTextSectionElement>();
        var remaining = text;

        while (remaining.Length > 0)
        {
            // Find the earliest inline match
            var (matchIndex, matchLength, element, beforeText) = FindEarliestInline(remaining);

            if (element is null)
            {
                // No more inline formatting — rest is plain text
                if (remaining.Length > 0)
                    elements.Add(new RichTextText { Text = remaining });
                break;
            }

            // Add plain text before the match
            if (!string.IsNullOrEmpty(beforeText))
                elements.Add(new RichTextText { Text = beforeText });

            elements.Add(element);
            remaining = remaining[(matchIndex + matchLength)..];
        }

        elements.RemoveAll(e => e is RichTextText textElement && string.IsNullOrEmpty(textElement.Text));

        return elements;
    }

    private static (int Index, int Length, RichTextSectionElement? Element, string? Before) FindEarliestInline(string text)
    {
        var best = (Index: int.MaxValue, Length: 0, Element: (RichTextSectionElement?)null, Before: (string?)null);

        // Bold+Italic: ***text***
        TryMatch(BoldItalicRegex(), text, ref best, (m) =>
            new RichTextText
            {
                Text = m.Groups[1].Value,
                Style = new RichTextStyle { Bold = true, Italic = true }
            });

        // Bold: **text**
        TryMatch(BoldRegex(), text, ref best, (m) =>
            new RichTextText
            {
                Text = m.Groups[1].Value,
                Style = new RichTextStyle { Bold = true }
            });

        // Italic: *text* (single star, not part of **)
        TryMatch(ItalicRegex(), text, ref best, (m) =>
            new RichTextText
            {
                Text = m.Groups[1].Value,
                Style = new RichTextStyle { Italic = true }
            });

        // Strikethrough: ~~text~~
        TryMatch(StrikethroughRegex(), text, ref best, (m) =>
            new RichTextText
            {
                Text = m.Groups[1].Value,
                Style = new RichTextStyle { Strike = true }
            });

        // Inline code: `text`
        TryMatch(InlineCodeRegex(), text, ref best, (m) =>
            new RichTextText
            {
                Text = m.Groups[1].Value,
                Style = new RichTextStyle { Code = true }
            });

        // Links: [text](url) — normalise the URL (recovers LLM-mangled
        // OAuth scope lists) then choose the right element. Safe URLs
        // become Block Kit RichTextLink (proper clickable Slack-native
        // link — addresses #850). Rewrite-prone URLs become inline-
        // code RichTextText so Slack's click redirector can't re-encode
        // them; the label is dropped because the URL has to be the
        // visible payload for copy. Uses SlackTextProtector's shared
        // markdown-link regex so both surfaces tokenize links identically.
        TryMatch(SlackTextProtector.MarkdownLinkRegex(), text, ref best, (m) =>
        {
            var url = SlackTextProtector.NormaliseScopeList(m.Groups[2].Value);
            return SlackTextProtector.IsRewriteProne(url)
                ? (RichTextSectionElement)new RichTextText
                {
                    Text = url,
                    Style = new RichTextStyle { Code = true }
                }
                : new RichTextLink
                {
                    Text = m.Groups[1].Value,
                    Url = url
                };
        });

        // Bare URLs: https://example.com — same is-it-safe-to-link
        // heuristic. Uses SlackTextProtector's shared bare-URL regex so
        // the Block Kit and plain-text surfaces tokenize URLs identically.
        TryMatch(SlackTextProtector.BareUrlRegex(), text, ref best, (m) =>
        {
            var url = SlackTextProtector.NormaliseScopeList(m.Value);
            return SlackTextProtector.IsRewriteProne(url)
                ? (RichTextSectionElement)new RichTextText
                {
                    Text = url,
                    Style = new RichTextStyle { Code = true }
                }
                : new RichTextLink { Url = url };
        });

        // Slack-native mentions passed through by the agent: <@U...>
        // user, <@subteam^S...> user group, <#C...> channel. Without
        // explicit rich-text elements the Block Kit surface renders
        // the syntax as literal text (the mrkdwn Text fallback handles
        // them natively, but blocks win on every modern client).
        TryMatch(UserMentionRegex(), text, ref best, (m) =>
            new RichTextUser { UserId = m.Groups[1].Value });

        TryMatch(UserGroupMentionRegex(), text, ref best, (m) =>
            new RichTextUserGroup { UserGroupId = m.Groups[1].Value });

        TryMatch(ChannelMentionRegex(), text, ref best, (m) =>
            new RichTextChannel { ChannelId = m.Groups[1].Value });

        if (best.Element is null)
            return (0, 0, null, null);

        return best;
    }

    private static void TryMatch(
        Regex regex,
        string text,
        ref (int Index, int Length, RichTextSectionElement? Element, string? Before) best,
        Func<Match, RichTextSectionElement> createElement)
    {
        var match = regex.Match(text);
        if (match.Success && match.Index < best.Index)
        {
            best = (
                match.Index,
                match.Length,
                createElement(match),
                match.Index > 0 ? text[..match.Index] : null
            );
        }
    }

    private static bool IsBulletListItem(string trimmed)
    {
        return (trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal))
            && !trimmed.StartsWith("**", StringComparison.Ordinal); // Don't match bold markers
    }

    private static string StripBulletPrefix(string trimmed)
    {
        if (trimmed.StartsWith("- ", StringComparison.Ordinal)) return trimmed[2..];
        if (trimmed.StartsWith("* ", StringComparison.Ordinal)) return trimmed[2..];
        return trimmed;
    }

    private static bool IsOrderedListItem(string trimmed)
    {
        return OrderedListPrefix().IsMatch(trimmed);
    }

    private static string StripOrderedPrefix(string trimmed)
    {
        var match = OrderedListPrefix().Match(trimmed);
        return match.Success ? trimmed[match.Length..] : trimmed;
    }

    private static string StripInlineMarkdown(string text)
    {
        // Remove ** and * markers for header plain text
        text = BoldRegex().Replace(text, "$1");
        text = ItalicRegex().Replace(text, "$1");
        return text;
    }

    // Bold+Italic: ***text***
    [GeneratedRegex(@"\*\*\*(.+?)\*\*\*")]
    private static partial Regex BoldItalicRegex();

    // Bold: **text**
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    // Italic: *text* (not preceded/followed by *)
    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)")]
    private static partial Regex ItalicRegex();

    // Strikethrough: ~~text~~
    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex StrikethroughRegex();

    // Inline code: `text`
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    // Ordered list prefix: 1. or 2. etc.
    [GeneratedRegex(@"^\d+\.\s")]
    private static partial Regex OrderedListPrefix();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex TableDividerCellRegex();

    // Slack user mention: <@U0123ABC> or <@W0123ABC>, optionally with a
    // fallback label (<@U0123ABC|david>).
    [GeneratedRegex(@"<@([UW][0-9A-Z]+)(?:\|[^>]+)?>")]
    private static partial Regex UserMentionRegex();

    // Slack user-group mention: <!subteam^S0123ABC> (the form Slack
    // documents and emits) or <@subteam^S0123ABC> (agent-style),
    // optionally with a fallback label (<!subteam^S0123ABC|@eng-team>).
    // Captures the bare group ID only — Slack's usergroup_id does not
    // include the subteam^ prefix.
    [GeneratedRegex(@"<[!@]subteam\^([0-9A-Z]+)(?:\|[^>]+)?>")]
    private static partial Regex UserGroupMentionRegex();

    // Slack channel mention: <#C0123ABC> or <#G0123ABC> (private channels
    // and group DMs), optionally with a label.
    [GeneratedRegex(@"<#([CGD][0-9A-Z]+)(?:\|[^>]+)?>")]
    private static partial Regex ChannelMentionRegex();

    private const int MaxTableRows = 100;
    private const int MaxTableColumns = 20;
    private const int MaxTableCharacters = 10_000;
}
