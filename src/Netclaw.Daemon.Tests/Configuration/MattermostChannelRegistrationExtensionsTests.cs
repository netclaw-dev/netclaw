// -----------------------------------------------------------------------
// <copyright file="MattermostChannelRegistrationExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Channels.Mattermost;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Tests.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class MattermostChannelRegistrationExtensionsTests
{
    [Fact]
    public void Invalid_server_url_does_not_throw_during_registration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory(new RecordingHandler(System.Net.HttpStatusCode.OK)));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mattermost:Enabled"] = "true",
                ["Mattermost:ServerUrl"] = "://not-a-uri",
                ["Mattermost:BotToken"] = "fake-token",
                ["Mattermost:AllowedChannelIds:0"] = "channel-1"
            })
            .Build();

        var ex = Record.Exception(() => services.AddMattermostChannelIntegration(configuration));

        Assert.Null(ex);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MattermostChannelOptions>();
        Assert.Equal("://not-a-uri", options.ServerUrl);
        Assert.Equal("fake-token", options.BotToken!.Value);
    }
}
