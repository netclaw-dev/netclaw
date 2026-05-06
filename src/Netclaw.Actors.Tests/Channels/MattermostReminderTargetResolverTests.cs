// -----------------------------------------------------------------------
// <copyright file="MattermostReminderTargetResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostReminderTargetResolverTests
{
    private readonly MattermostReminderTargetResolver _resolver = new();

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    [InlineData("@abcdefghijklmnopqrstuvwxyz")]
    public async Task Resolves_user_targets_to_canonical_user_id(string input)
    {
        var result = await _resolver.ResolveAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.User, result.Kind);
        Assert.Equal("abcdefghijklmnopqrstuvwxyz", result.ResolvedId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Resolves_channel_prefix_to_channel_target()
    {
        var result = await _resolver.ResolveAsync(
            "channel:abcdefghijklmnopqrstuvwxyz",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.Channel, result.Kind);
        Assert.Equal("abcdefghijklmnopqrstuvwxyz", result.ResolvedId);
    }

    [Fact]
    public async Task Rejects_short_non_mattermost_ids()
    {
        var result = await _resolver.ResolveAsync("@aaron", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("Could not resolve Mattermost target", result.ErrorMessage);
    }

    [Fact]
    public async Task Rejects_empty_target()
    {
        var result = await _resolver.ResolveAsync("", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("required", result.ErrorMessage);
    }

    [Fact]
    public async Task Rejects_invalid_channel_id()
    {
        var result = await _resolver.ResolveAsync("channel:short", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Invalid Mattermost channel ID", result.ErrorMessage);
    }

    [Fact]
    public void Transport_is_mattermost()
    {
        Assert.Equal("mattermost", _resolver.Transport);
    }
}
