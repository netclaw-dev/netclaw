// -----------------------------------------------------------------------
// <copyright file="SlackAclContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Slack;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class SlackAclContractTests : AclPolicyContractTests
{
    protected override string ExpectedSourceKind => "slack";

    protected override IAclDecision EvaluateDm(string userId, ChannelOptionsBuilder options)
        => EvaluateMessage("dm-channel", userId, isDm: true, options);

    protected override IAclDecision EvaluateChannel(
        string channelId, string userId, ChannelOptionsBuilder options)
        => EvaluateMessage(channelId, userId, isDm: false, options);

    protected override IAclDecision EvaluateMessage(
        string channelId, string userId, bool isDm, ChannelOptionsBuilder options)
    {
        var slackOptions = new SlackChannelOptions
        {
            AllowDirectMessages = options.AllowDirectMessages,
            AllowedChannelIds = options.AllowedChannelIds,
            AllowedUserIds = options.AllowedUserIds,
            ChannelAudiences = options.ChannelAudiences
        };

        var slackUserId = string.IsNullOrEmpty(userId) ? null : (SlackUserId?)new SlackUserId(userId);

        var message = new SlackInboundMessage(
            Kind: SlackInboundKind.Message,
            EventId: new SlackEventId("evt-1"),
            ChannelId: new SlackChannelId(channelId),
            ThreadTs: null,
            EventTs: new SlackEventTs("1234567890.000001"),
            UserId: slackUserId,
            BotId: null,
            Text: "test",
            Subtype: null,
            Hidden: false,
            IsDirectMessage: isDm);

        var defaultChannelId = options.DefaultChannelId is not null
            ? new SlackChannelId(options.DefaultChannelId)
            : (SlackChannelId?)null;

        return SlackAclPolicy.EvaluateInbound(message, slackOptions, defaultChannelId);
    }
}
