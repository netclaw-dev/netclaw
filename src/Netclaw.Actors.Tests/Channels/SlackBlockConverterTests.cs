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
    public static TheoryData<string> SupportedMarkdown =>
        new()
        {
            "Plain text",
            "[Netclaw](https://netclaw.dev/docs)",
            "https://netclaw.dev/docs",
            "**[Netclaw](https://netclaw.dev/docs)**",
            "[**Netclaw**](https://netclaw.dev/docs)",
            "```csharp\nvar value = GetValue();\n```",
            "| Service | Status |\n| --- | --- |\n| API | Healthy |",
            "- [x] Complete\n- [ ] Pending"
        };

    [Theory]
    [MemberData(nameof(SupportedMarkdown))]
    public void Convert_preserves_supported_markdown(string markdown)
    {
        var blocks = SlackBlockConverter.Convert(markdown);

        var block = Assert.IsType<MarkdownBlock>(Assert.Single(blocks));
        Assert.Equal(markdown, block.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Convert_returns_no_blocks_for_empty_markdown(string markdown)
    {
        Assert.Empty(SlackBlockConverter.Convert(markdown));
    }

    [Fact]
    public void Convert_accepts_the_Slack_markdown_limit()
    {
        var markdown = new string('a', 12_000);

        var block = Assert.IsType<MarkdownBlock>(Assert.Single(SlackBlockConverter.Convert(markdown)));
        Assert.Equal(markdown, block.Text);
    }

    [Fact]
    public void Convert_uses_the_text_fallback_above_the_Slack_markdown_limit()
    {
        var markdown = new string('a', 12_001);

        Assert.Empty(SlackBlockConverter.Convert(markdown));
    }
}
