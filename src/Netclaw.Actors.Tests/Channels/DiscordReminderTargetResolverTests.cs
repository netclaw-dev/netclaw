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
    private readonly DiscordReminderTargetResolver _resolver = new();

    [Fact]
    public async Task Resolves_channel_mention_to_canonical_channel_id()
    {
        var result = await _resolver.ResolveAsync("<#129847561203948576>", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.Channel, result.Kind);
        Assert.Equal("129847561203948576", result.ResolvedId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Resolves_explicit_channel_prefix_to_canonical_channel_id()
    {
        var result = await _resolver.ResolveAsync("channel:129847561203948576", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.Channel, result.Kind);
        Assert.Equal("129847561203948576", result.ResolvedId);
        Assert.Null(result.ErrorMessage);
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
    public async Task Rejects_user_targets_while_discord_reminders_are_channel_only(string input)
    {
        var result = await _resolver.ResolveAsync(input, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("channel", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not supported", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_dm_channel_prefix_while_discord_reminders_are_channel_only()
    {
        var result = await _resolver.ResolveAsync("dm:130111223344556677", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("DM", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_non_canonical_discord_targets()
    {
        var result = await _resolver.ResolveAsync("@aaron", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("channel:<channelId>", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
