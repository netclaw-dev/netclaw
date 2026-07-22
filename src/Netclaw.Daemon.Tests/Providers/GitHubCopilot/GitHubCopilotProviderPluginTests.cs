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
    private static CopilotTokenExchanger ExchangerReturning(string copilotToken, string? apiBase)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(apiBase is null
                    ? new
                    {
                        token = copilotToken,
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                    }
                    : (object)new
                    {
                        token = copilotToken,
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                        endpoints = new { api = apiBase },
                    }),
                Encoding.UTF8,
                "application/json"),
        });
        return new CopilotTokenExchanger(new HttpClient(handler));
    }

    private static CopilotTokenExchanger NewExchanger() => ExchangerReturning("copilot-token", apiBase: null);

    private static GitHubCopilotProviderPlugin NewPlugin()
    {
        var exchanger = NewExchanger();
        var descriptor = DescriptorWithModels(exchanger, "/chat/completions");
        return new GitHubCopilotProviderPlugin(descriptor, exchanger);
    }

    private static GitHubCopilotDescriptor DescriptorWithModels(
        CopilotTokenExchanger exchanger, params string[] endpoints)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                data = new[]
                {
                    new
                    {
                        id = "gpt-4o",
                        capabilities = new { type = "chat" },
                        supported_endpoints = endpoints,
                    },
                },
            }), Encoding.UTF8, "application/json"),
        });
        return new GitHubCopilotDescriptor(new HttpClient(handler), exchanger);
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

        var exchanger = ExchangerReturning("copilot-real", apiBase: "https://api.githubcopilot.com");
        var descriptor = DescriptorWithModels(exchanger, "/chat/completions");
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

    [Fact]
    public async Task CreateChatClient_RoutesChatToTokenApiHost()
    {
        // End-to-end proof for issue #1550: the entry is configured with the
        // public endpoint (the default), but the GHE token reports a tenant host
        // in endpoints.api. The fully-assembled request that reaches the wire
        // must target the tenant host, not api.githubcopilot.com — otherwise
        // Copilot rejects the tenant token with HTTP 400.
        Uri? sentUri = null;
        var captureHandler = new FakeHttpMessageHandler(req =>
        {
            sentUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    MinimalChatCompletionJson, Encoding.UTF8, "application/json"),
            };
        });

        var exchanger = ExchangerReturning("copilot-ghe", apiBase: "https://api.tenant.ghe.com");
        var descriptor = DescriptorWithModels(exchanger, "/chat/completions");
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

        Assert.NotNull(sentUri);
        Assert.Equal("api.tenant.ghe.com", sentUri!.Host);
        Assert.Equal("/chat/completions", sentUri.AbsolutePath);
    }

    [Fact]
    public async Task CreateChatClient_CustomEndpoint_KeepsOperatorHostOverToken()
    {
        // A deliberate proxy override must win over the token's endpoints.api so
        // the operator's traffic is never silently rerouted off their proxy.
        Uri? sentUri = null;
        var captureHandler = new FakeHttpMessageHandler(req =>
        {
            sentUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    MinimalChatCompletionJson, Encoding.UTF8, "application/json"),
            };
        });

        var exchanger = ExchangerReturning("copilot-ghe", apiBase: "https://api.tenant.ghe.com");
        var descriptor = DescriptorWithModels(exchanger, "/chat/completions");
        var plugin = new GitHubCopilotProviderPlugin(descriptor, exchanger)
        {
            TransportOverride = new HttpClientPipelineTransport(new HttpClient(captureHandler)),
        };

        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("oauth-1"),
            Endpoint = "https://copilot-proxy.example.com",
        };
        var client = plugin.CreateChatClient(
            entry, new ModelReference { Provider = "my-copilot", ModelId = "gpt-4o" });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(sentUri);
        Assert.Equal("copilot-proxy.example.com", sentUri!.Host);
    }

    [Fact]
    public async Task CreateChatClient_ResponsesOnlyModel_UsesResponsesAtTokenHostWithCopilotHeaders()
    {
        Uri? sentUri = null;
        HttpRequestMessage? sentRequest = null;
        var captureHandler = new FakeHttpMessageHandler(req =>
        {
            sentUri = req.RequestUri;
            sentRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MinimalResponseJson, Encoding.UTF8, "application/json"),
            };
        });

        var exchanger = ExchangerReturning("copilot-ghe", apiBase: "https://api.tenant.ghe.com");
        var descriptor = DescriptorWithModels(exchanger, "/responses", "ws:/responses");
        var plugin = new GitHubCopilotProviderPlugin(descriptor, exchanger)
        {
            TransportOverride = new HttpClientPipelineTransport(new HttpClient(captureHandler)),
        };
        var entry = new ProviderEntry
        {
            Type = "github-copilot", AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("oauth-1"),
        };

        var client = plugin.CreateChatClient(
            entry, new ModelReference { Provider = "my-copilot", ModelId = "gpt-4o" });
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(sentUri);
        Assert.Equal("api.tenant.ghe.com", sentUri!.Host);
        Assert.Equal("/responses", sentUri.AbsolutePath);
        Assert.Equal("Bearer copilot-ghe", sentRequest!.Headers.Authorization!.ToString());
        Assert.Equal("vscode-chat", sentRequest.Headers.GetValues("copilot-integration-id").Single());
        Assert.True(sentRequest.Headers.Contains("editor-version"));
        Assert.Equal("conversation-agent", sentRequest.Headers.GetValues("openai-intent").Single());
        var userAgent = sentRequest.Headers.UserAgent.ToString();
        Assert.StartsWith(NetclawUserAgent.Value, userAgent);
        Assert.Equal(1, userAgent.Split(NetclawUserAgent.Value, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task CreateChatClient_ModelWithoutHttpEndpoint_FailsBeforeInference()
    {
        var inferenceCalls = 0;
        var exchanger = ExchangerReturning("copilot-ghe", apiBase: "https://api.tenant.ghe.com");
        var descriptor = DescriptorWithModels(exchanger, "ws:/responses");
        var plugin = new GitHubCopilotProviderPlugin(descriptor, exchanger)
        {
            TransportOverride = new HttpClientPipelineTransport(new HttpClient(
                new FakeHttpMessageHandler(_ =>
                {
                    Interlocked.Increment(ref inferenceCalls);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }))),
        };
        var entry = new ProviderEntry
        {
            Type = "github-copilot", AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("oauth-1"),
        };
        var client = plugin.CreateChatClient(
            entry, new ModelReference { Provider = "my-copilot", ModelId = "gpt-4o" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not advertise a supported HTTP inference endpoint", error.Message);
        Assert.Equal(0, inferenceCalls);
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

    private const string MinimalResponseJson =
        """
        {
          "id": "resp-test",
          "object": "response",
          "created_at": 0,
          "status": "completed",
          "model": "gpt-4o",
          "output": [
            {
              "id": "msg-test",
              "type": "message",
              "status": "completed",
              "role": "assistant",
              "content": [ { "type": "output_text", "text": "ok", "annotations": [] } ]
            }
          ]
        }
        """;
}
