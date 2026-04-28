// -----------------------------------------------------------------------
// <copyright file="DiscordAclContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Discord;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class DiscordAclContractTests : AclPolicyContractTests
{
    protected override string ExpectedSourceKind => "discord";

    protected override IAclDecision EvaluateDm(string userId, ChannelOptionsBuilder options)
        => EvaluateMessage("dm-channel", userId, isDm: true, options);

    protected override IAclDecision EvaluateChannel(
        string channelId, string userId, ChannelOptionsBuilder options)
        => EvaluateMessage(channelId, userId, isDm: false, options);

    protected override IAclDecision EvaluateMessage(
        string channelId, string userId, bool isDm, ChannelOptionsBuilder options)
    {
        var discordOptions = new DiscordChannelOptions
        {
            AllowDirectMessages = options.AllowDirectMessages,
            AllowedChannelIds = options.AllowedChannelIds,
            AllowedUserIds = options.AllowedUserIds,
            ChannelAudiences = options.ChannelAudiences
        };

        var message = new DiscordGatewayMessage(
            EventId: new DiscordEventId("evt-1"),
            ChannelId: new DiscordChannelId(channelId),
            ReplyChannelId: new DiscordReplyChannelId(channelId),
            MessageId: new DiscordMessageId("msg-1"),
            ThreadOrMessageId: new DiscordThreadOrMessageId("thread-1"),
            RootMessageId: null,
            SenderId: new DiscordUserId(userId),
            IsBotMessage: false,
            IsDirectMessage: isDm,
            ContainsBotMention: false,
            Text: "test",
            ReceivedAt: TimeProvider.System.GetUtcNow());

        var defaultChannelId = options.DefaultChannelId is not null
            ? new DiscordChannelId(options.DefaultChannelId)
            : (DiscordChannelId?)null;

        return DiscordAclPolicy.EvaluateInbound(message, discordOptions, defaultChannelId);
    }
}
