// -----------------------------------------------------------------------
// <copyright file="DiscordReminderTargetResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Netclaw.Channels.Discord;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordReminderTargetResolverTests
{
    private const string ChannelId = "129847561203948576";
    private const string UserId = "130111223344556677";

    private readonly DiscordReminderTargetResolver _resolver = new(new DiscordChannelOptions
    {
        AllowDirectMessages = true,
        AllowedChannelIds = [ChannelId],
        AllowedUserIds = [UserId]
    });

    [Fact]
    public async Task Resolves_channel_mention_to_canonical_channel_id()
    {
        var result = await _resolver.ResolveAsync($"<#{ChannelId}>", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.Channel, result.Kind);
        Assert.Equal(ChannelId, result.ResolvedId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Resolves_explicit_channel_prefix_to_canonical_channel_id()
    {
        var result = await _resolver.ResolveAsync($"channel:{ChannelId}", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.Channel, result.Kind);
        Assert.Equal(ChannelId, result.ResolvedId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Rejects_channel_targets_outside_allowed_channels()
    {
        var result = await _resolver.ResolveAsync("channel:129847561203948577", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("allowed channels", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_bare_snowflakes_because_they_are_ambiguous()
    {
        var result = await _resolver.ResolveAsync("129847561203948576", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("ambiguous", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("@129847561203948576")]
    [InlineData("<@129847561203948576>")]
    [InlineData("<@!129847561203948576>")]
    [InlineData("dm:129847561203948576")]
    public async Task Resolves_explicit_user_targets_to_canonical_user_id(string input)
    {
        var resolver = new DiscordReminderTargetResolver(new DiscordChannelOptions
        {
            AllowDirectMessages = true,
            AllowedUserIds = ["129847561203948576"]
        });

        var result = await resolver.ResolveAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.User, result.Kind);
        Assert.Equal("129847561203948576", result.ResolvedId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Rejects_user_targets_when_direct_messages_are_disabled()
    {
        var resolver = new DiscordReminderTargetResolver(new DiscordChannelOptions
        {
            AllowDirectMessages = false,
            AllowedUserIds = [UserId]
        });

        var result = await resolver.ResolveAsync($"dm:{UserId}", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("disabled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_user_targets_outside_allowed_users()
    {
        var result = await _resolver.ResolveAsync("dm:130111223344556678", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("allowed users", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_invalid_direct_message_target()
    {
        var result = await _resolver.ResolveAsync("dm:not-a-user-id", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("direct-message", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_non_canonical_discord_targets()
    {
        var result = await _resolver.ResolveAsync("@aaron", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("user target", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
