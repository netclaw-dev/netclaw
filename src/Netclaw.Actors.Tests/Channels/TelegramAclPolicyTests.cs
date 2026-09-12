// -----------------------------------------------------------------------
// <copyright file="TelegramAclPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramAclPolicyTests
{
    [Fact]
    public void Direct_message_is_denied_when_disabled()
    {
        var result = TelegramAclPolicy.EvaluateInbound(Message(true), new TelegramChannelOptions());
        Assert.False(result.IsAllowed);
        Assert.Equal(AclDenyReasons.DirectMessagesDisabled, result.DenyReason);
    }

    [Fact]
    public void Allowed_direct_message_is_accepted()
    {
        var options = new TelegramChannelOptions { AllowDirectMessages = true };
        Assert.True(TelegramAclPolicy.EvaluateInbound(Message(true), options).IsAllowed);
    }

    [Fact]
    public void Group_message_requires_allowed_chat()
    {
        var result = TelegramAclPolicy.EvaluateInbound(Message(false), new TelegramChannelOptions());
        Assert.False(result.IsAllowed);
        Assert.Equal(AclDenyReasons.ChannelNotAllowed, result.DenyReason);
    }

    [Fact]
    public void Allowed_group_and_user_are_accepted()
    {
        var options = new TelegramChannelOptions
        {
            AllowedChatIds = ["123"],
            AllowedUserIds = ["456"]
        };
        Assert.True(TelegramAclPolicy.EvaluateInbound(Message(false), options).IsAllowed);
    }

    [Fact]
    public void Unknown_user_is_denied_when_user_list_is_restricted()
    {
        var options = new TelegramChannelOptions { AllowDirectMessages = true, AllowedUserIds = ["999"] };
        var result = TelegramAclPolicy.EvaluateInbound(Message(true), options);
        Assert.False(result.IsAllowed);
        Assert.Equal(AclDenyReasons.UserNotAllowed, result.DenyReason);
    }

    [Fact]
    public void Missing_user_is_denied()
    {
        var message = Message(true) with { UserId = null };
        var result = TelegramAclPolicy.EvaluateInbound(
            message, new TelegramChannelOptions { AllowDirectMessages = true });
        Assert.False(result.IsAllowed);
        Assert.Equal(AclDenyReasons.MissingUserId, result.DenyReason);
    }

    private static TelegramInboundMessage Message(bool isDirect) =>
        new(123, 456, 1, "hello", isDirect);
}
