using Netclaw.Actors.Reminders;
using Netclaw.Channels.Discord;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordReminderTargetResolverTests
{
    private readonly DiscordReminderTargetResolver _resolver = new();

    [Theory]
    [InlineData("129847561203948576")]
    [InlineData("@129847561203948576")]
    [InlineData("<@129847561203948576>")]
    [InlineData("<@!129847561203948576>")]
    public async Task Resolves_user_targets_to_canonical_user_id(string input)
    {
        var result = await _resolver.ResolveAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.User, result.Kind);
        Assert.Equal("129847561203948576", result.ResolvedId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Resolves_dm_channel_prefix_to_channel_target()
    {
        var result = await _resolver.ResolveAsync("dm:130111223344556677", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(ReminderTargetKind.Channel, result.Kind);
        Assert.Equal("130111223344556677", result.ResolvedId);
    }

    [Fact]
    public async Task Rejects_non_canonical_discord_targets()
    {
        var result = await _resolver.ResolveAsync("@aaron", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ReminderTargetKind.Unknown, result.Kind);
        Assert.Null(result.ResolvedId);
        Assert.Contains("Could not resolve Discord target", result.ErrorMessage);
    }
}
