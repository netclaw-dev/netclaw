// -----------------------------------------------------------------------
// <copyright file="SlackBlockConverterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Slack;
using SlackNet.Blocks;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public class SlackBlockConverterTests
{
    [Fact]
    public void PlainText_ProducesSingleRichTextSection()
    {
        var blocks = SlackBlockConverter.Convert("Hello, world!");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());
        var text = Assert.Single(section.Elements.OfType<RichTextText>());
        Assert.Equal("Hello, world!", text.Text);
    }

    [Fact]
    public void BoldText_ProducesStyledElement()
    {
        var blocks = SlackBlockConverter.Convert("This is **important** stuff");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());
        var elements = section.Elements.OfType<RichTextText>().ToList();

        Assert.Equal(3, elements.Count);
        Assert.Equal("This is ", elements[0].Text);
        Assert.False(elements[0].Style?.Bold ?? false);

        Assert.Equal("important", elements[1].Text);
        Assert.True(elements[1].Style?.Bold);

        Assert.Equal(" stuff", elements[2].Text);
    }

    [Fact]
    public void ItalicText_ProducesStyledElement()
    {
        var blocks = SlackBlockConverter.Convert("This is *emphasized* text");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());
        var elements = section.Elements.OfType<RichTextText>().ToList();

        Assert.Equal("emphasized", elements[1].Text);
        Assert.True(elements[1].Style?.Italic);
    }

    [Fact]
    public void InlineCode_ProducesCodeStyledElement()
    {
        var blocks = SlackBlockConverter.Convert("Use the `web_search` tool");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());
        var elements = section.Elements.OfType<RichTextText>().ToList();

        Assert.Equal("web_search", elements[1].Text);
        Assert.True(elements[1].Style?.Code);
    }

    [Fact]
    public void MarkdownLink_WithSafeUrl_ProducesRichTextLink()
    {
        // Closes #850 at the Block Kit surface: standard markdown
        // [text](url) is converted into a Slack-native RichTextLink
        // (with label) so it renders as a clickable link.
        var blocks = SlackBlockConverter.Convert("Visit [Google](https://google.com) for search");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var link = Assert.Single(section.Elements.OfType<RichTextLink>());
        Assert.Equal("https://google.com", link.Url);
        Assert.Equal("Google", link.Text);
    }

    [Fact]
    public void MarkdownLink_WithRewriteProneUrl_ProducesInlineCode()
    {
        // Closes #1092 at the Block Kit surface: markdown links with
        // '+' in the URL would be re-encoded by Slack's link redirector
        // on click. Render as inline code so the URL is non-clickable
        // and survives. Label is dropped because the URL has to be the
        // visible payload.
        var blocks = SlackBlockConverter.Convert(
            "Auth at [here](https://accounts.google.com/o/oauth2/auth?scope=A+B+C&state=1).");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        Assert.Empty(section.Elements.OfType<RichTextLink>());
        var codeElement = Assert.Single(
            section.Elements.OfType<RichTextText>(),
            t => t.Style?.Code == true);
        Assert.Equal(
            "https://accounts.google.com/o/oauth2/auth?scope=A+B+C&state=1",
            codeElement.Text);
    }

    [Fact]
    public void MarkdownLink_WithMisencodedScopeList_DecodedAndProducesInlineCode()
    {
        // Closes #1092 LLM-rewrite shape: bot URL-encoded the literal
        // '+' between scopes into '%2B' when constructing the markdown
        // link. The Block path decodes them back to '+' and emits the
        // URL as inline code.
        var blocks = SlackBlockConverter.Convert(
            "Auth at [here](https://x.example.com/auth?scope=A%2BB%2BC&state=1).");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        Assert.Empty(section.Elements.OfType<RichTextLink>());
        var codeElement = Assert.Single(
            section.Elements.OfType<RichTextText>(),
            t => t.Style?.Code == true);
        Assert.Equal(
            "https://x.example.com/auth?scope=A+B+C&state=1",
            codeElement.Text);
    }

    [Fact]
    public void MarkdownLink_UrlWithParentheses_NotTruncated()
    {
        // Regression for #1107: a balanced '(...)' inside the link
        // destination must stay part of the URL — the destination is
        // not truncated at the first ')'.
        var blocks = SlackBlockConverter.Convert(
            "Read [the article](https://en.wikipedia.org/wiki/Foo_(disambiguation)) now.");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var link = Assert.Single(section.Elements.OfType<RichTextLink>());
        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(disambiguation)", link.Url);
        Assert.Equal("the article", link.Text);
    }

    [Fact]
    public void BareUrl_WithRewriteProneUrl_ProducesInlineCode()
    {
        // Bare URL with '+' — block path emits inline code, not a link.
        var blocks = SlackBlockConverter.Convert(
            "Visit https://accounts.google.com/auth?scope=A+B+C for auth");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        Assert.Empty(section.Elements.OfType<RichTextLink>());
        var codeElement = Assert.Single(
            section.Elements.OfType<RichTextText>(),
            t => t.Style?.Code == true);
        Assert.Equal(
            "https://accounts.google.com/auth?scope=A+B+C",
            codeElement.Text);
    }

    [Fact]
    public void Header_ProducesHeaderBlock()
    {
        var blocks = SlackBlockConverter.Convert("## 128GB AMD AI Mini PCs\n\nHere are the options:");

        var header = Assert.Single(blocks.OfType<HeaderBlock>());
        Assert.Equal("128GB AMD AI Mini PCs", header.Text.Text);

        // The paragraph after the header should be a rich text block
        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());
        var text = Assert.Single(section.Elements.OfType<RichTextText>());
        Assert.Equal("Here are the options:", text.Text);
    }

    [Fact]
    public void SubHeaders_ProduceHeaderBlocks()
    {
        var blocks = SlackBlockConverter.Convert("### 1. MINISFORUM MS-S1 MAX");

        var header = Assert.Single(blocks.OfType<HeaderBlock>());
        Assert.Equal("1. MINISFORUM MS-S1 MAX", header.Text.Text);
    }

    [Fact]
    public void CodeBlock_ProducesRichTextPreformatted()
    {
        var input = """
            Here's an example:
            ```
            var x = 42;
            Console.WriteLine(x);
            ```
            """;

        var blocks = SlackBlockConverter.Convert(input);

        var rtb = blocks.OfType<RichTextBlock>().ToList();
        // Should have at least one RichTextBlock containing a preformatted element
        var preformatted = rtb.SelectMany(b => b.Elements).OfType<RichTextPreformatted>().ToList();
        Assert.Single(preformatted);
        var codeText = string.Join("", preformatted[0].Elements.OfType<RichTextText>().Select(e => e.Text));
        Assert.Contains("var x = 42;", codeText);
    }

    [Fact]
    public void BulletList_ProducesRichTextList()
    {
        var input = """
            Options:
            - **Price:** ~$1,499
            - **Where:** [Amazon](https://amazon.com)
            - **Status:** Available now
            """;

        var blocks = SlackBlockConverter.Convert(input);

        var rtb = blocks.OfType<RichTextBlock>().ToList();
        var lists = rtb.SelectMany(b => b.Elements).OfType<RichTextList>().ToList();
        Assert.Single(lists);
        Assert.Equal(RichTextListStyle.Bullet, lists[0].Style);
        Assert.Equal(3, lists[0].Elements.Count);
    }

    [Fact]
    public void OrderedList_ProducesOrderedRichTextList()
    {
        var input = """
            1. First item
            2. Second item
            3. Third item
            """;

        var blocks = SlackBlockConverter.Convert(input);

        var rtb = blocks.OfType<RichTextBlock>().ToList();
        var lists = rtb.SelectMany(b => b.Elements).OfType<RichTextList>().ToList();
        Assert.Single(lists);
        Assert.Equal(RichTextListStyle.Ordered, lists[0].Style);
        Assert.Equal(3, lists[0].Elements.Count);
    }

    [Fact]
    public void MarkdownTable_ProducesTableBlockWithRawTextCells()
    {
        var input = """
            | Name | Status |
            | --- | --- |
            | API | Healthy |
            | Worker | Degraded |
            """;

        var blocks = SlackBlockConverter.Convert(input);

        var table = Assert.Single(blocks.OfType<TableBlock>());
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(2, table.Rows[0].Count);
        Assert.Equal("Name", Assert.IsType<RawTextCell>(table.Rows[0][0]).Text);
        Assert.Equal("Healthy", Assert.IsType<RawTextCell>(table.Rows[1][1]).Text);
        Assert.Equal("Degraded", Assert.IsType<RawTextCell>(table.Rows[2][1]).Text);
    }

    [Fact]
    public void MarkdownTable_WithEscapedPipe_PreservesCellText()
    {
        var input = """
            | Expression | Value |
            | --- | --- |
            | a \| b | true |
            """;

        var blocks = SlackBlockConverter.Convert(input);

        var table = Assert.Single(blocks.OfType<TableBlock>());
        Assert.Equal("a | b", Assert.IsType<RawTextCell>(table.Rows[1][0]).Text);
    }

    [Fact]
    public void MarkdownTable_WithInlineMarkdown_ProducesRichTextCells()
    {
        var input = """
            | Item | Guide |
            | --- | --- |
            | **Deploy** | [Read the guide](https://example.com/guide) |
            """;

        var blocks = SlackBlockConverter.Convert(input);

        var table = Assert.Single(blocks.OfType<TableBlock>());
        var itemCell = Assert.IsType<RichTextCell>(table.Rows[1][0]);
        var itemSection = Assert.Single(itemCell.Elements.OfType<RichTextSection>());
        var itemText = Assert.Single(itemSection.Elements.OfType<RichTextText>());
        Assert.Equal("Deploy", itemText.Text);
        Assert.True(itemText.Style?.Bold);

        var guideCell = Assert.IsType<RichTextCell>(table.Rows[1][1]);
        var guideSection = Assert.Single(guideCell.Elements.OfType<RichTextSection>());
        var guideLink = Assert.Single(guideSection.Elements.OfType<RichTextLink>());
        Assert.Equal("Read the guide", guideLink.Text);
        Assert.Equal("https://example.com/guide", guideLink.Url);
    }

    [Fact]
    public void MarkdownTable_WithMoreThanTwentyColumns_RemainsRichText()
    {
        var header = string.Join(" | ", Enumerable.Range(1, 21).Select(column => $"H{column}"));
        var divider = string.Join(" | ", Enumerable.Repeat("---", 21));
        var row = string.Join(" | ", Enumerable.Range(1, 21).Select(column => $"V{column}"));

        var blocks = SlackBlockConverter.Convert($"{header}\n{divider}\n{row}");

        Assert.Empty(blocks.OfType<TableBlock>());
        var richText = Assert.Single(blocks.OfType<RichTextBlock>());
        Assert.Equal(3, richText.Elements.OfType<RichTextSection>().Count());
    }

    [Fact]
    public void MarkdownTable_WithMoreThanOneHundredRows_RemainsRichText()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 100).Select(row => $"| {row} |"));
        var input = $"| Value |\n| --- |\n{rows}";

        var blocks = SlackBlockConverter.Convert(input);

        Assert.Empty(blocks.OfType<TableBlock>());
    }

    [Fact]
    public void MarkdownTable_AfterFirstTable_RemainsRichText()
    {
        var input = """
            | Name |
            | --- |
            | API |

            | Name |
            | --- |
            | Worker |
            """;

        var blocks = SlackBlockConverter.Convert(input);

        Assert.Single(blocks.OfType<TableBlock>());
        var richText = Assert.Single(blocks.OfType<RichTextBlock>());
        Assert.Contains(
            richText.Elements.OfType<RichTextSection>(),
            section => section.Elements.OfType<RichTextText>().Any(text => text.Text == "| Worker |"));
    }

    [Fact]
    public void Blockquote_ProducesRichTextQuote()
    {
        var blocks = SlackBlockConverter.Convert("> This is a quoted passage");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var quote = Assert.Single(rtb.Elements.OfType<RichTextQuote>());
        var text = Assert.Single(quote.Elements.OfType<RichTextText>());
        Assert.Equal("This is a quoted passage", text.Text);
    }

    [Fact]
    public void Strikethrough_ProducesStrikeStyle()
    {
        var blocks = SlackBlockConverter.Convert("This is ~~wrong~~ correct");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());
        var elements = section.Elements.OfType<RichTextText>().ToList();

        Assert.Equal("wrong", elements[1].Text);
        Assert.True(elements[1].Style?.Strike);
    }

    [Fact]
    public void BoldWithinListItem_PreservesFormatting()
    {
        var input = "- **Price:** ~$1,499 - $1,899 USD";

        var blocks = SlackBlockConverter.Convert(input);

        var rtb = blocks.OfType<RichTextBlock>().ToList();
        var list = rtb.SelectMany(b => b.Elements).OfType<RichTextList>().Single();
        var item = list.Elements[0];
        var boldElement = item.Elements.OfType<RichTextText>().First(e => e.Style?.Bold == true);
        Assert.Equal("Price:", boldElement.Text);
    }

    [Fact]
    public void RealWorldLlmOutput_ConvertsCorrectly()
    {
        // Simulates the actual LLM output from the screenshot
        var input = """
            ## 128GB AMD Ryzen AI Mini PCs (Ryzen AI Max+ 395)

            ### 1. MINISFORUM MS-S1 MAX
            - **Price:** ~$1,499 - $1,899 USD
            - **Where:** [Amazon](https://www.amazon.com/MINISFORUM-AMD-Ryzen-Max-395/dp/B0G2VJR4JD)
            - **Specs:** Ryzen AI Max+ 395 (16-core), **128GB LPDDR5x-8000 unified**, 2TB SSD, 16TOPS NPU
            - **Status:** Available now
            """;

        var blocks = SlackBlockConverter.Convert(input);

        // Should have header blocks and rich text blocks
        var headers = blocks.OfType<HeaderBlock>().ToList();
        Assert.True(headers.Count >= 2, $"Expected at least 2 headers, got {headers.Count}");
        Assert.Equal("128GB AMD Ryzen AI Mini PCs (Ryzen AI Max+ 395)", headers[0].Text.Text);
        Assert.Equal("1. MINISFORUM MS-S1 MAX", headers[1].Text.Text);

        // Should have a bullet list
        var rtBlocks = blocks.OfType<RichTextBlock>().ToList();
        var lists = rtBlocks.SelectMany(b => b.Elements).OfType<RichTextList>().ToList();
        Assert.Single(lists);
        Assert.Equal(4, lists[0].Elements.Count);

        // First list item should contain a link
        var firstItem = lists[0].Elements[0];
        // The Amazon link should be in one of the items
        var allLinks = lists[0].Elements
            .SelectMany(e => e.Elements.OfType<RichTextLink>())
            .ToList();
        Assert.Single(allLinks);
        Assert.Contains("amazon.com", allLinks[0].Url);
    }

    [Fact]
    public void MultipleConsecutiveParagraphs_ProduceSeparateSections()
    {
        var input = """
            First paragraph here.

            Second paragraph here.
            """;

        var blocks = SlackBlockConverter.Convert(input);

        var rtb = blocks.OfType<RichTextBlock>().ToList();
        // Both paragraphs should produce sections
        var sections = rtb.SelectMany(b => b.Elements).OfType<RichTextSection>().ToList();
        Assert.Equal(2, sections.Count);
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyBlocks()
    {
        var blocks = SlackBlockConverter.Convert("");
        Assert.Empty(blocks);
    }

    [Fact]
    public void BareUrl_ProducesRichTextLink()
    {
        var blocks = SlackBlockConverter.Convert("Check out https://github.com/foo/bar for details");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var link = Assert.Single(section.Elements.OfType<RichTextLink>());
        Assert.Equal("https://github.com/foo/bar", link.Url);
        Assert.Null(link.Text);
    }

    [Fact]
    public void BareUrl_AtSentenceEnd_ExcludesTrailingPeriodFromLink()
    {
        // Shared bare-URL tokenizer (SlackTextProtector.BareUrlRegex):
        // the sentence's closing period must not be pulled into the
        // link target — it survives as trailing plain text.
        var blocks = SlackBlockConverter.Convert("Check https://example.com.");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var link = Assert.Single(section.Elements.OfType<RichTextLink>());
        Assert.Equal("https://example.com", link.Url);

        Assert.Contains(section.Elements.OfType<RichTextText>(), t => t.Text == ".");
    }

    [Fact]
    public void BareUrl_InListItem_ProducesRichTextLink()
    {
        var input = "- See https://example.com/docs for more info";

        var blocks = SlackBlockConverter.Convert(input);

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var list = Assert.Single(rtb.Elements.OfType<RichTextList>());
        var item = list.Elements[0];

        var link = Assert.Single(item.Elements.OfType<RichTextLink>());
        Assert.Equal("https://example.com/docs", link.Url);
    }

    [Fact]
    public void MarkdownLink_TakesPriorityOverBareUrl()
    {
        var blocks = SlackBlockConverter.Convert("Visit [Google](https://google.com) today");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var link = Assert.Single(section.Elements.OfType<RichTextLink>());
        Assert.Equal("https://google.com", link.Url);
        Assert.Equal("Google", link.Text);
    }

    [Fact]
    public void BoldAndItalicNested_HandledCorrectly()
    {
        var blocks = SlackBlockConverter.Convert("This is ***bold and italic*** text");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());
        var styled = section.Elements.OfType<RichTextText>()
            .FirstOrDefault(e => e.Style is { Bold: true, Italic: true });
        Assert.NotNull(styled);
        Assert.Equal("bold and italic", styled.Text);
    }

    [Fact]
    public void UserMention_ProducesRichTextUser()
    {
        var blocks = SlackBlockConverter.Convert("Ping <@U01SU57E553> when ready");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var user = Assert.Single(section.Elements.OfType<RichTextUser>());
        Assert.Equal("U01SU57E553", user.UserId);

        var texts = section.Elements.OfType<RichTextText>().ToList();
        Assert.Equal("Ping ", texts[0].Text);
        Assert.Equal(" when ready", texts[1].Text);
    }

    [Fact]
    public void UserMention_WithLabel_ProducesRichTextUser()
    {
        var blocks = SlackBlockConverter.Convert("Thanks <@U01SU57E553|david>!");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var user = Assert.Single(section.Elements.OfType<RichTextUser>());
        Assert.Equal("U01SU57E553", user.UserId);
    }

    [Fact]
    public void UserGroupMention_ProducesRichTextUserGroup()
    {
        var blocks = SlackBlockConverter.Convert("Heads up <@subteam^S0123ABC>");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var group = Assert.Single(section.Elements.OfType<RichTextUserGroup>());
        Assert.Equal("S0123ABC", group.UserGroupId);
    }

    [Fact]
    public void UserGroupMention_WithLabel_ProducesRichTextUserGroup()
    {
        var blocks = SlackBlockConverter.Convert("Heads up <@subteam^S0123ABC|@eng-team>");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var group = Assert.Single(section.Elements.OfType<RichTextUserGroup>());
        Assert.Equal("S0123ABC", group.UserGroupId);
    }

    [Fact]
    public void UserGroupMention_DocumentedBangForm_ProducesRichTextUserGroup()
    {
        var blocks = SlackBlockConverter.Convert("Heads up <!subteam^S0123ABC>");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var group = Assert.Single(section.Elements.OfType<RichTextUserGroup>());
        Assert.Equal("S0123ABC", group.UserGroupId);
    }

    [Fact]
    public void ChannelMention_ProducesRichTextChannel()
    {
        var blocks = SlackBlockConverter.Convert("Posted in <#C0AM51E342X> already");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var channel = Assert.Single(section.Elements.OfType<RichTextChannel>());
        Assert.Equal("C0AM51E342X", channel.ChannelId);
    }

    [Fact]
    public void ChannelMention_PrivateChannel_ProducesRichTextChannel()
    {
        var blocks = SlackBlockConverter.Convert("See <#G0AM51E342X|private>");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var channel = Assert.Single(section.Elements.OfType<RichTextChannel>());
        Assert.Equal("G0AM51E342X", channel.ChannelId);
    }

    [Fact]
    public void UserMention_InListItem_ProducesRichTextUser()
    {
        var blocks = SlackBlockConverter.Convert("- David: approve <@U01SU57E553>");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var list = Assert.Single(rtb.Elements.OfType<RichTextList>());
        var item = list.Elements[0];

        var user = Assert.Single(item.Elements.OfType<RichTextUser>());
        Assert.Equal("U01SU57E553", user.UserId);
    }
}
