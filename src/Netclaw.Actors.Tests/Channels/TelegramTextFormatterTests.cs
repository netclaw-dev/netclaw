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
}
