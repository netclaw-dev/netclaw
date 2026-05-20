// -----------------------------------------------------------------------
// <copyright file="MattermostAclContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class MattermostAclContractTests : AclPolicyContractTests
{
    protected override string ExpectedSourceKind => "mattermost";

    protected override IAclDecision EvaluateDm(string userId, ChannelOptionsBuilder options)
        => EvaluateMessage("dm-channel", userId, isDm: true, options);

    protected override IAclDecision EvaluateChannel(
        string channelId, string userId, ChannelOptionsBuilder options)
        => EvaluateMessage(channelId, userId, isDm: false, options);

    protected override IAclDecision EvaluateMessage(
        string channelId, string userId, bool isDm, ChannelOptionsBuilder options)
    {
        var mattermostOptions = new MattermostChannelOptions
        {
            AllowDirectMessages = options.AllowDirectMessages,
            AllowedChannelIds = options.AllowedChannelIds,
            AllowedUserIds = options.AllowedUserIds,
            ChannelAudiences = options.ChannelAudiences
        };

        var message = new MattermostGatewayMessage(
            EventId: new MattermostEventId("evt-1"),
            ChannelId: new MattermostChannelId(channelId),
            PostId: new MattermostPostId("post-1"),
            RootPostId: new MattermostRootPostId(string.Empty),
            SenderId: new MattermostUserId(userId),
            IsBotMessage: false,
            IsDirectMessage: isDm,
            ContainsBotMention: false,
            Text: "test",
            ReceivedAt: TimeProvider.System.GetUtcNow());

        var defaultChannelId = options.DefaultChannelId is not null
            ? new MattermostChannelId(options.DefaultChannelId)
            : (MattermostChannelId?)null;

        return MattermostAclPolicy.EvaluateInbound(message, mattermostOptions, defaultChannelId);
    }
}
