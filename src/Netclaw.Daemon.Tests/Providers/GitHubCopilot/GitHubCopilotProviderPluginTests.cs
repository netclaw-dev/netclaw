// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotProviderPluginTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            OAuthAccessToken = new SensitiveString("gho_1"),
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
            OAuthAccessToken = new SensitiveString("gho_1"),
            Endpoint = "https://copilot-proxy.example.com/",
        };
        var model = new ModelReference { Provider = "my-copilot", ModelId = "gpt-4o" };

        var client = plugin.CreateChatClient(entry, model);

        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateChatClient_CopilotApiBaseVendorOption_UsesConfiguredEndpoint()
    {
        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("gho_1"),
        };
        entry.SetVendorOptions(new JsonObject
        {
            ["CopilotApiBase"] = "https://copilot-api.example.ghe.com",
        });

        var capture = await CaptureStreamingRequestAsync(entry, options: null);

        Assert.Equal("https://copilot-api.example.ghe.com/chat/completions", capture.Uri);
    }

    [Fact]
    public async Task CreateChatClient_DefaultEndpointWithCopilotApiBaseVendorOption_UsesConfiguredEndpoint()
    {
        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("gho_1"),
            Endpoint = "https://api.githubcopilot.com",
        };
        entry.SetVendorOptions(new JsonObject
        {
            ["CopilotApiBase"] = "https://copilot-api.example.ghe.com",
        });

        var capture = await CaptureStreamingRequestAsync(entry, options: null);

        Assert.Equal("https://copilot-api.example.ghe.com/chat/completions", capture.Uri);
    }

    [Fact]
    public async Task CreateChatClient_CustomEndpointWithCopilotApiBaseVendorOption_UsesCustomEndpoint()
    {
        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("gho_1"),
            Endpoint = "https://copilot-proxy.example.com/",
        };
        entry.SetVendorOptions(new JsonObject
        {
            ["CopilotApiBase"] = "https://copilot-api.example.ghe.com",
        });

        var capture = await CaptureStreamingRequestAsync(entry, options: null);

        Assert.Equal("https://copilot-proxy.example.com/chat/completions", capture.Uri);
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
            OAuthAccessToken = new SensitiveString("gho_1"),
        };
        var client = plugin.CreateChatClient(
            entry, new ModelReference { Provider = "my-copilot", ModelId = "gpt-4o" });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Bearer copilot-real", sentAuthorization);
    }

    [Fact]
    public async Task CreateChatClient_StreamingRequest_UsesCopilotWireContract()
    {
        var capture = await CaptureStreamingRequestAsync(options: null);

        Assert.Equal(HttpMethod.Post, capture.Method);
        Assert.Equal("https://api.githubcopilot.com/chat/completions", capture.Uri);
        Assert.Equal("Bearer copilot-real", capture.Authorization);
        Assert.Equal("vscode-chat", capture.Header("copilot-integration-id"));
        Assert.Equal($"Netclaw/{BuildInfo.Version}", capture.Header("editor-version"));
        Assert.Equal($"netclaw/{BuildInfo.Version}", capture.Header("Editor-Plugin-Version"));
        Assert.Null(capture.Header("X-GitHub-Api-Version"));
        Assert.Equal("conversation-agent", capture.Header("openai-intent"));

        using var body = JsonDocument.Parse(capture.Body);
        Assert.Equal("gpt-4o", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task CreateChatClient_StreamingRequest_WithTools_SendsToolPayload()
    {
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string query) => query, "search_docs", "Search docs")]
        };

        var capture = await CaptureStreamingRequestAsync(options);

        using var body = JsonDocument.Parse(capture.Body);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        var tool = body.RootElement.GetProperty("tools").EnumerateArray().Single();
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("search_docs", tool.GetProperty("function").GetProperty("name").GetString());
    }

    private static async Task<CapturedRequest> CaptureStreamingRequestAsync(ChatOptions? options)
        => await CaptureStreamingRequestAsync(new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("gho_1"),
        }, options);

    private static async Task<CapturedRequest> CaptureStreamingRequestAsync(ProviderEntry entry, ChatOptions? options)
    {
        CapturedRequest? captured = null;
        var captureHandler = new FakeHttpMessageHandler(req =>
        {
            captured = CapturedRequest.From(req);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    MinimalStreamingChatCompletion, Encoding.UTF8, "text/event-stream"),
            };
        });

        var exchanger = ExchangerReturning("copilot-real");
        var descriptor = new GitHubCopilotDescriptor(new HttpClient(), exchanger);
        var plugin = new GitHubCopilotProviderPlugin(descriptor, exchanger)
        {
            TransportOverride = new HttpClientPipelineTransport(new HttpClient(captureHandler)),
        };

        var client = plugin.CreateChatClient(
            entry, new ModelReference { Provider = "my-copilot", ModelId = "gpt-4o" });

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            options,
            TestContext.Current.CancellationToken))
        {
        }

        return captured ?? throw new InvalidOperationException("The streaming request was not captured.");
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        Dictionary<string, string> Headers,
        string Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);

        public static CapturedRequest From(HttpRequestMessage request)
        {
            var headers = request.Headers
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            return new CapturedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                headers,
                request.Content?.ReadAsStringAsync(TestContext.Current.CancellationToken)
                    .GetAwaiter()
                    .GetResult() ?? string.Empty);
        }
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

    private const string MinimalStreamingChatCompletion =
        """
        data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":0,"model":"gpt-4o","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":null}]}

        data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":0,"model":"gpt-4o","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: [DONE]

        """;
}
