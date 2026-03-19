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

        // Links: [text](url)
        TryMatch(LinkRegex(), text, ref best, (m) =>
            new RichTextLink
            {
                Text = m.Groups[1].Value,
                Url = m.Groups[2].Value
            });

        // Bare URLs: https://example.com
        TryMatch(BareUrlRegex(), text, ref best, (m) =>
            new RichTextLink { Url = m.Value });

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

    // Links: [text](url)
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex LinkRegex();

    // Bare URLs: https://example.com
    [GeneratedRegex(@"https?://[^\s)\]>]+")]
    private static partial Regex BareUrlRegex();

    // Ordered list prefix: 1. or 2. etc.
    [GeneratedRegex(@"^\d+\.\s")]
    private static partial Regex OrderedListPrefix();
}
