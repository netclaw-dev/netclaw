// -----------------------------------------------------------------------
// <copyright file="MattermostChannelOptionsDefaultsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Configuration;

public sealed class MattermostChannelOptionsDefaultsTests
{
    [Fact]
    public void BindsSecureDefaults_WhenMattermostSectionMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var options = configuration.GetSection("Mattermost").Get<MattermostChannelOptions>() ?? new MattermostChannelOptions();

        Assert.False(options.Enabled);
        Assert.False(options.AllowDirectMessages);
        Assert.Null(options.ServerUrl);
        Assert.Empty(options.AllowedChannelIds);
        Assert.Empty(options.AllowedUserIds);
    }

    [Fact]
    public void KeepsSecureDefaults_WhenMattermostSectionPartiallyConfigured()
    {
        var values = new Dictionary<string, string?>
        {
            ["Mattermost:Enabled"] = "true",
            ["Mattermost:ServerUrl"] = "https://mattermost.example.com",
            ["Mattermost:DefaultChannelId"] = "abcdefghij1234567890abcdef"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = configuration.GetSection("Mattermost").Get<MattermostChannelOptions>() ?? new MattermostChannelOptions();

        Assert.True(options.Enabled);
        Assert.Equal("https://mattermost.example.com", options.ServerUrl);
        Assert.Equal("abcdefghij1234567890abcdef", options.DefaultChannelId);
        Assert.False(options.AllowDirectMessages);
        Assert.Empty(options.AllowedChannelIds);
        Assert.Empty(options.AllowedUserIds);
    }
}
