// -----------------------------------------------------------------------
// <copyright file="SlackBlockConverter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SlackNet.Blocks;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Converts model Markdown to a Slack Markdown block.
/// </summary>
public static class SlackBlockConverter
{
    private const int MaxMarkdownBlockCharacters = 12_000;

    /// <summary>
    /// Converts Markdown text to Slack blocks.
    /// </summary>
    public static List<Block> Convert(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        // Slack limits all Markdown blocks in one message to 12,000 characters.
        // The caller uses the message text when this method returns no blocks.
        if (markdown.Length > MaxMarkdownBlockCharacters)
            return [];

        return [new MarkdownBlock { Text = markdown }];
    }
}
