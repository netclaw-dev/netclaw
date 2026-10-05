// -----------------------------------------------------------------------
// <copyright file="ApprovalCommandThresholdTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Slack;
using SlackNet.Blocks;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// The authorizer sends a command to a prompt only when its text has no more
/// than <see cref="ApprovalOptionKeys.MaxCommandTextChars"/> characters. Every
/// channel must then show that full text, also in the largest prompt.
/// </summary>
public sealed class ApprovalCommandThresholdTests
{
    [Fact]
    public void Command_at_the_threshold_shows_in_full_on_every_channel()
    {
        const string head = "gh api repos/o/r/pulls -f body=";
        var command = head + new string('b', ApprovalOptionKeys.MaxCommandTextChars - head.Length);
        var request = LongApprovalCommand.Request(withSiblingVerbs: true) with
        {
            DisplayText = command,
            Patterns = ["git push", command],
            CandidateVerbs = ["git push", command],
            IsMessy = true,
            HasAdoptedContext = true,
            AdoptedSpeakerIds = ["U111", "U222", "U333"]
        };

        var (discord, _) = DiscordApprovalPromptBuilder.BuildButtonPrompt(request);
        var discordText = DiscordApprovalPromptBuilder.BuildTextPrompt(request);
        var slack = SlackApprovalBlockBuilder.BuildApprovalBlocks(request)
            .OfType<SectionBlock>()
            .Select(static block => ((Markdown)block.Text).Text);
        var (mattermost, _) = MattermostApprovalPromptBuilder.BuildButtonPrompt(
            request, "https://callback.example/url", channelId: "ch-1", rootPostId: "root-1", promptCorrelationId: "prompt-1");

        Assert.All([discord, discordText], message => Assert.InRange(message.Length, 1, DiscordApprovalPromptBuilder.MaxMessageChars));
        Assert.All(
            [discord, discordText, string.Join('\n', slack), mattermost],
            message => Assert.Contains(command, message, StringComparison.Ordinal));
    }
}
