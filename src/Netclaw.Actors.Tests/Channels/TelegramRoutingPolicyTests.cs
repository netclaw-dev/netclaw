// -----------------------------------------------------------------------
// <copyright file="TelegramRoutingPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Telegram;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramRoutingPolicyTests
{
    [Fact]
    public void Direct_message_does_not_require_mention()
    {
        var result = TelegramRoutingPolicy.Evaluate(Message(isDirect: true), mentionOnly: true);
        Assert.Equal(TelegramRoutingDecisionKind.StartOrContinue, result.Kind);
    }

    [Fact]
    public void Group_mention_starts_or_continues()
    {
        var result = TelegramRoutingPolicy.Evaluate(
            Message() with { ContainsBotMention = true },
            mentionOnly: true);
        Assert.Equal(TelegramRoutingDecisionKind.StartOrContinue, result.Kind);
    }

    [Fact]
    public void Reply_to_bot_starts_or_continues()
    {
        var result = TelegramRoutingPolicy.Evaluate(
            Message() with { IsReplyToBot = true },
            mentionOnly: true);
        Assert.Equal(TelegramRoutingDecisionKind.StartOrContinue, result.Kind);
    }

    [Fact]
    public void Unmentioned_group_message_is_ignored()
    {
        var result = TelegramRoutingPolicy.Evaluate(Message(), mentionOnly: true);
        Assert.Equal(TelegramRoutingDecisionKind.Ignore, result.Kind);
        Assert.Equal(TelegramRoutingIgnoreReason.GroupMentionRequired, result.IgnoreReason);
    }

    [Fact]
    public void Mention_only_false_accepts_group_message()
    {
        var result = TelegramRoutingPolicy.Evaluate(Message(), mentionOnly: false);
        Assert.Equal(TelegramRoutingDecisionKind.StartOrContinue, result.Kind);
    }

    [Fact]
    public void Empty_message_without_files_is_ignored()
    {
        var result = TelegramRoutingPolicy.Evaluate(Message() with { Text = "" }, mentionOnly: false);
        Assert.Equal(TelegramRoutingDecisionKind.Ignore, result.Kind);
        Assert.Equal(TelegramRoutingIgnoreReason.NoContent, result.IgnoreReason);
    }

    private static TelegramInboundMessage Message(bool isDirect = false) =>
        new(123, 456, 1, "hello", isDirect);
}
