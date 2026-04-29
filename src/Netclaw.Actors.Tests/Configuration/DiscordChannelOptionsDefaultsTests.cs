// -----------------------------------------------------------------------
// <copyright file="DiscordChannelOptionsDefaultsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Channels.Discord;
using Xunit;

namespace Netclaw.Actors.Tests.Configuration;

public sealed class DiscordChannelOptionsDefaultsTests
{
    [Fact]
    public void BindsSecureDefaults_WhenDiscordSectionMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var options = configuration.GetSection("Discord").Get<DiscordChannelOptions>() ?? new DiscordChannelOptions();

        Assert.False(options.Enabled);
        Assert.False(options.AllowDirectMessages);
        Assert.Empty(options.AllowedChannelIds);
        Assert.Empty(options.AllowedUserIds);
    }

    [Fact]
    public void KeepsSecureDefaults_WhenDiscordSectionPartiallyConfigured()
    {
        var values = new Dictionary<string, string?>
        {
            ["Discord:Enabled"] = "true",
            ["Discord:DefaultChannelId"] = "123456789"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = configuration.GetSection("Discord").Get<DiscordChannelOptions>() ?? new DiscordChannelOptions();

        Assert.True(options.Enabled);
        Assert.Equal("123456789", options.DefaultChannelId);
        Assert.False(options.AllowDirectMessages);
        Assert.Empty(options.AllowedChannelIds);
        Assert.Empty(options.AllowedUserIds);
    }
}
