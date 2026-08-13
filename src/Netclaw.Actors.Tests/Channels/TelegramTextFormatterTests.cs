// -----------------------------------------------------------------------
// <copyright file="TelegramTextFormatterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Telegram;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramTextFormatterTests
{
    [Fact]
    public void Converts_common_markdown_and_escapes_html()
    {
        var result = TelegramTextFormatter.ToHtml(
            "**bold** *italic* `code` [site](https://example.com) <unsafe>");

        Assert.Equal(
            "<b>bold</b> <i>italic</i> <code>code</code> "
            + "<a href=\"https://example.com\">site</a> &lt;unsafe&gt;",
            result);
    }

    [Fact]
    public void Does_not_create_unsafe_links()
    {
        var result = TelegramTextFormatter.ToHtml("[click](javascript:alert)");

        Assert.Equal("[click](javascript:alert)", result);
    }

    [Fact]
    public void Converts_combined_bold_and_italic_without_broken_html()
    {
        var result = TelegramTextFormatter.ToHtml(
            "***Bold + Italic:*** ***Lumen***");

        Assert.Equal(
            "<b><i>Bold + Italic:</i></b> <b><i>Lumen</i></b>",
            result);
    }

    [Fact]
    public void Converts_headings_lists_quotes_and_strikethrough()
    {
        var result = TelegramTextFormatter.ToHtml(
            "## Heading\n\n- First **item**\n* Second item\n1. Ordered item\n> A *quote*\n~~removed~~");

        Assert.Equal(
            "<b>Heading</b>\n\n• First <b>item</b>\n• Second item\n1. Ordered item\n"
            + "<blockquote>A <i>quote</i></blockquote>\n<s>removed</s>",
            result);
    }

    [Fact]
    public void Does_not_format_markdown_inside_code()
    {
        var result = TelegramTextFormatter.ToHtml(
            "Before `**inline** <tag>`\n```text\n# heading\n**bold** <tag>\n```\nAfter **bold**");

        Assert.Equal(
            "Before <code>**inline** &lt;tag&gt;</code>\n"
            + "<pre><code># heading\n**bold** &lt;tag&gt;\n</code></pre>\n"
            + "After <b>bold</b>",
            result);
    }

    [Fact]
    public void Preserves_numbered_list_markers_and_formats_contents()
    {
        var result = TelegramTextFormatter.ToHtml("1. **First**\n2. ~~Second~~");

        Assert.Equal("1. <b>First</b>\n2. <s>Second</s>", result);
    }
}
