// -----------------------------------------------------------------------
// <copyright file="ChatClientFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.Anthropic;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ProviderPluginFactoryTests
{
    private static readonly HttpClient SharedHttp = new();

    private static readonly ILlmProviderPlugin[] AllPlugins =
    [
        new OllamaProviderPlugin(new OllamaDescriptor(SharedHttp)),
        new OpenAiProviderPlugin(new OpenAiDescriptor(SharedHttp)),
        new AnthropicProviderPlugin(new AnthropicDescriptor(SharedHttp)),
        new OpenRouterProviderPlugin(new OpenRouterDescriptor(SharedHttp)),
    ];

    [Fact]
    public void CreatesOllamaClient()
    {
        var factory = CreateFactory(("local", new ProviderEntry
        {
            Type = "ollama",
            Endpoint = "http://localhost:11434"
        }));

        var client = factory.Create(new ModelReference
        {
            Provider = "local",
            ModelId = "qwen3:30b"
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void CreatesOpenAIClient()
    {
        var factory = CreateFactory(("my-openai", new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.ApiKey,
            ApiKey = new SensitiveString("sk-test-fake-key")
        }));

        var client = factory.Create(new ModelReference
        {
            Provider = "my-openai",
            ModelId = "gpt-4o-mini"
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void CreatesAnthropicClient()
    {
        var factory = CreateFactory(("my-anthropic", new ProviderEntry
        {
            Type = "anthropic",
            AuthMethod = AuthMethod.ApiKey,
            ApiKey = new SensitiveString("sk-ant-test-fake-key")
        }));

        var client = factory.Create(new ModelReference
        {
            Provider = "my-anthropic",
            ModelId = "claude-sonnet-4-20250514"
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void CreatesOpenRouterClient()
    {
        var factory = CreateFactory(("my-openrouter", new ProviderEntry
        {
            Type = "openrouter",
            Endpoint = "https://openrouter.ai/api/v1",
            AuthMethod = AuthMethod.ApiKey,
            ApiKey = new SensitiveString("sk-or-test-fake-key")
        }));

        var client = factory.Create(new ModelReference
        {
            Provider = "my-openrouter",
            ModelId = "anthropic/claude-sonnet-4-20250514"
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void CreatesOpenRouterClient_WithDefaultEndpoint()
    {
        var factory = CreateFactory(("or", new ProviderEntry
        {
            Type = "openrouter",
            Endpoint = "", // empty — should use default
            AuthMethod = AuthMethod.ApiKey,
            ApiKey = new SensitiveString("sk-or-test-fake-key")
        }));

        var client = factory.Create(new ModelReference
        {
            Provider = "or",
            ModelId = "google/gemini-2.5-pro"
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void CreatesClient_WithOAuthToken_WhenNoApiKey()
    {
        var factory = CreateFactory(("oauth-provider", new ProviderEntry
        {
            Type = "anthropic",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("oauth-test-token")
        }));

        var client = factory.Create(new ModelReference
        {
            Provider = "oauth-provider",
            ModelId = "claude-sonnet-4-20250514"
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void ThrowsForUnknownProvider()
    {
        var factory = CreateFactory(("x", new ProviderEntry
        {
            Type = "unknown-provider"
        }));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(new ModelReference { Provider = "x", ModelId = "m" }));

        Assert.Contains("Unknown provider type", ex.Message);
        Assert.Contains("unknown-provider", ex.Message);
    }

    [Fact]
    public void ThrowsForMissingProviderName()
    {
        var factory = CreateFactory(("existing", new ProviderEntry
        {
            Type = "ollama"
        }));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(new ModelReference { Provider = "nonexistent", ModelId = "m" }));

        Assert.Contains("not found", ex.Message);
        Assert.Contains("existing", ex.Message);
    }

    [Fact]
    public void ThrowsForMissingCredentials()
    {
        var factory = CreateFactory(("no-creds", new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.ApiKey
            // No ApiKey or OAuthAccessToken set
        }));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(new ModelReference { Provider = "no-creds", ModelId = "gpt-4o" }));

        Assert.Contains("requires authentication", ex.Message);
    }

    [Fact]
    public void OllamaVendorOptions_CanDisableThinking()
    {
        var plugin = new OllamaProviderPlugin(new OllamaDescriptor(SharedHttp));
        var entry = new ProviderEntry
        {
            Type = "ollama",
            VendorOptions = JsonNode.Parse("""
                {
                  "DisableThinking": true
                }
                """)!.AsObject()
        };

        var source = plugin.CreateVendorOptionsSource(entry);

        Assert.NotNull(source);

        var options = new ChatOptions();
        source!.Apply(options);

        Assert.Equal(false, options.AdditionalProperties?["think"]);
    }

    [Fact]
    public void OpenRouterVendorOptions_CanDisableReasoningExclusion()
    {
        var plugin = new OpenRouterProviderPlugin(new OpenRouterDescriptor(SharedHttp));
        var entry = new ProviderEntry
        {
            Type = "openrouter",
            VendorOptions = JsonNode.Parse("""
                {
                  "ExcludeReasoning": false
                }
                """)!.AsObject()
        };

        Assert.Null(plugin.CreateVendorOptionsSource(entry));
    }

    private static ProviderPluginFactory CreateFactory(params (string name, ProviderEntry entry)[] providers)
    {
        var dict = new Dictionary<string, ProviderEntry>();
        foreach (var (name, entry) in providers)
            dict[name] = entry;
        return new ProviderPluginFactory(dict, AllPlugins);
    }
}
