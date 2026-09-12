// -----------------------------------------------------------------------
// <copyright file="DiscordChannelContextTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Discord.Transport;
using static Netclaw.Channels.Discord.Transport.DiscordNetGatewayClient;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordChannelContextTests
{
    [Fact]
    public void DM_messages_with_different_ids_produce_same_session_key()
    {
        ulong dmChannelId = 111;

        var (_, _, threadOrMsg1) = ResolveChannelContext(
            dmChannelId, messageId: 1001, DiscordChannelKind.DirectMessage, parentChannelId: null);

        var (_, _, threadOrMsg2) = ResolveChannelContext(
            dmChannelId, messageId: 1002, DiscordChannelKind.DirectMessage, parentChannelId: null);

        Assert.Equal(threadOrMsg1, threadOrMsg2);
        Assert.Equal(dmChannelId.ToString(), threadOrMsg1);
    }

    [Fact]
    public void Channel_messages_with_different_ids_produce_different_session_keys()
    {
        ulong channelId = 222;

        var (_, _, threadOrMsg1) = ResolveChannelContext(
            channelId, messageId: 2001, DiscordChannelKind.GuildChannel, parentChannelId: null);

        var (_, _, threadOrMsg2) = ResolveChannelContext(
            channelId, messageId: 2002, DiscordChannelKind.GuildChannel, parentChannelId: null);

        Assert.NotEqual(threadOrMsg1, threadOrMsg2);
    }

    [Fact]
    public void Thread_messages_use_thread_channel_id_as_session_key()
    {
        ulong threadChannelId = 333;
        ulong parentChannelId = 444;

        var (channelId, _, threadOrMsg) = ResolveChannelContext(
            threadChannelId, messageId: 3001, DiscordChannelKind.Thread, parentChannelId: parentChannelId);

        Assert.Equal(parentChannelId.ToString(), channelId);
        Assert.Equal(threadChannelId.ToString(), threadOrMsg);
    }
}
