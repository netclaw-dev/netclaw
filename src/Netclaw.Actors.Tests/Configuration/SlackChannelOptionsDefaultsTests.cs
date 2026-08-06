// -----------------------------------------------------------------------
// <copyright file="SlackChannelOptionsDefaultsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Channels.Slack;
using Xunit;

namespace Netclaw.Actors.Tests.Configuration;

public sealed class SlackChannelOptionsDefaultsTests
{
    [Fact]
    public void BindsSecureDefaults_WhenSlackSectionMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var options = configuration.GetSection("Slack").Get<SlackChannelOptions>() ?? new SlackChannelOptions();

        Assert.False(options.Enabled);
        Assert.True(options.SocketMode);
        Assert.True(options.MentionOnly);
        Assert.False(options.AllowDirectMessages);
        Assert.False(options.MentionRequiredInDm);
        Assert.False(options.MentionRequiredInThread);
        Assert.Empty(options.AllowedChannelIds);
        Assert.Empty(options.AllowedUserIds);
    }

    [Fact]
    public void KeepsSecureDefaults_WhenSlackSectionPartiallyConfigured()
    {
        var values = new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Slack:DefaultChannelName"] = "openclaw"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = configuration.GetSection("Slack").Get<SlackChannelOptions>() ?? new SlackChannelOptions();

        Assert.True(options.Enabled);
        Assert.Equal("openclaw", options.DefaultChannelName);
        Assert.True(options.MentionOnly);
        Assert.False(options.AllowDirectMessages);
        Assert.False(options.MentionRequiredInDm);
        Assert.False(options.MentionRequiredInThread);
        Assert.Empty(options.AllowedChannelIds);
        Assert.Empty(options.AllowedUserIds);
    }
}
