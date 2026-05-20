// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotProviderPluginTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.GitHubCopilot;

public sealed class GitHubCopilotProviderPluginTests
{
    private static CopilotTokenExchanger NewExchanger()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = "copilot-token",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                }),
                Encoding.UTF8,
                "application/json"),
        });
        return new CopilotTokenExchanger(new HttpClient(handler));
    }

    private static GitHubCopilotProviderPlugin NewPlugin()
    {
        var exchanger = NewExchanger();
        var descriptor = new GitHubCopilotDescriptor(new HttpClient(), exchanger);
        return new GitHubCopilotProviderPlugin(descriptor, exchanger);
    }

    [Fact]
    public void CreateChatClient_DefaultEndpoint_ReturnsNonNullClient()
    {
        var plugin = NewPlugin();
        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("oauth-1"),
        };
        var model = new ModelReference { Provider = "my-copilot", ModelId = "gpt-4o" };

        var client = plugin.CreateChatClient(entry, model);

        Assert.NotNull(client);
    }

    [Fact]
    public void CreateChatClient_CustomEndpoint_DoesNotThrow()
    {
        // Operators may point the entry at a corporate proxy in front of
        // api.githubcopilot.com. The plugin must respect the override and
        // not double-up the trailing slash.
        var plugin = NewPlugin();
        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("oauth-1"),
            Endpoint = "https://copilot-proxy.example.com/",
        };
        var model = new ModelReference { Provider = "my-copilot", ModelId = "gpt-4o" };

        var client = plugin.CreateChatClient(entry, model);

        Assert.NotNull(client);
    }

    [Fact]
    public void Plugin_AdvertisesGitHubCopilotTypeKey()
    {
        var plugin = NewPlugin();
        Assert.Equal("github-copilot", plugin.TypeKey);
        Assert.Equal("GitHub Copilot", plugin.DisplayName);
    }
}
