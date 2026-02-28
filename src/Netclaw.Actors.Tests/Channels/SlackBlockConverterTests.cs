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
    public void MarkdownLink_ProducesRichTextLink()
    {
        var blocks = SlackBlockConverter.Convert("Visit [Google](https://google.com) for search");

        var rtb = Assert.Single(blocks.OfType<RichTextBlock>());
        var section = Assert.Single(rtb.Elements.OfType<RichTextSection>());

        var link = Assert.Single(section.Elements.OfType<RichTextLink>());
        Assert.Equal("https://google.com", link.Url);
        Assert.Equal("Google", link.Text);
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
}
