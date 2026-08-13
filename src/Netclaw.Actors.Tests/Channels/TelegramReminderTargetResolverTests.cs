// -----------------------------------------------------------------------
// <copyright file="TelegramReminderTargetResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Netclaw.Channels.Telegram;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramReminderTargetResolverTests
{
    private const string UserId = "123456789";
    private const string ChatId = "-1001234567890";

    private readonly TelegramReminderTargetResolver _resolver = new(new TelegramChannelOptions
    {
        AllowDirectMessages = true,
        AllowedUserIds = [UserId],
        AllowedChatIds = [ChatId]
    });

    [Fact]
    public async Task Resolves_positive_allowed_id_as_user()
    {
        var result = await _resolver.ResolveAsync(UserId, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.User, result.Kind);
        Assert.Equal(UserId, result.ResolvedId);
    }

    [Fact]
    public async Task Resolves_negative_allowed_id_as_chat()
    {
        var result = await _resolver.ResolveAsync(ChatId, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.Channel, result.Kind);
        Assert.Equal(ChatId, result.ResolvedId);
    }

    [Fact]
    public async Task Rejects_user_when_direct_messages_are_disabled()
    {
        var resolver = new TelegramReminderTargetResolver(new TelegramChannelOptions
        {
            AllowDirectMessages = false,
            AllowedUserIds = [UserId]
        });

        var result = await resolver.ResolveAsync(UserId, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("disabled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_user_outside_allowed_users()
    {
        var result = await _resolver.ResolveAsync("987654321", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("allowed users", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_chat_outside_allowed_chats()
    {
        var result = await _resolver.ResolveAsync("-1009876543210", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("allowed chats", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("telegram-user")]
    public async Task Rejects_invalid_target(string target)
    {
        var result = await _resolver.ResolveAsync(target, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
    }
}
