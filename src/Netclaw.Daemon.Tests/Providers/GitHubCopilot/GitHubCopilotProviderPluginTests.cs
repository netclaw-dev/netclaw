// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotProviderPluginTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.GitHubCopilot;

public sealed class GitHubCopilotProviderPluginTests
{
    private static CopilotTokenExchanger ExchangerReturning(string copilotToken)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = copilotToken,
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                }),
                Encoding.UTF8,
                "application/json"),
        });
        return new CopilotTokenExchanger(new HttpClient(handler));
    }

    private static CopilotTokenExchanger NewExchanger() => ExchangerReturning("copilot-token");

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

    [Fact]
    public async Task CreateChatClient_SendsExchangedTokenNotPlaceholder()
    {
        // Regression: the OpenAI SDK's credential auth policy runs after our
        // CopilotRequestPolicy and writes Authorization from the shared
        // ApiKeyCredential. The earlier implementation set the header in the
        // policy and the SDK overwrote it with "Bearer placeholder", so Copilot
        // returned 400 "Authorization header is badly formatted". This drives
        // the real OpenAI SDK pipeline through a capturing transport and asserts
        // the exchanged token — not the placeholder — reaches the wire. A pure
        // policy unit test cannot catch this; the bug lives in the pipeline
        // ordering between our policy and the SDK's credential policy.
        string? sentAuthorization = null;
        var captureHandler = new FakeHttpMessageHandler(req =>
        {
            sentAuthorization = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    MinimalChatCompletionJson, Encoding.UTF8, "application/json"),
            };
        });

        var exchanger = ExchangerReturning("copilot-real");
        var descriptor = new GitHubCopilotDescriptor(new HttpClient(), exchanger);
        var plugin = new GitHubCopilotProviderPlugin(descriptor, exchanger)
        {
            TransportOverride = new HttpClientPipelineTransport(new HttpClient(captureHandler)),
        };

        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("oauth-1"),
        };
        var client = plugin.CreateChatClient(
            entry, new ModelReference { Provider = "my-copilot", ModelId = "gpt-4o" });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Bearer copilot-real", sentAuthorization);
    }

    private const string MinimalChatCompletionJson =
        """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 0,
          "model": "gpt-4o",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "ok" },
              "finish_reason": "stop"
            }
          ],
          "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
        }
        """;
}
