// -----------------------------------------------------------------------
// <copyright file="ChatClientFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Tests.Providers;
using Netclaw.Daemon.Tests.Providers.GitHubCopilot;
using Netclaw.Providers;
using Netclaw.Providers.Anthropic;
using Netclaw.Providers.DeepSeek;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;
using Netclaw.Providers.VeniceAi;
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

    // Regression guard: every client the factory returns must carry the
    // ModelRequestSettingsChatClient guarantee (own options copy, the model settings, and
    // NetclawChatOptionKeys.SuppressReasoning always stripped before the wire). The factory is the single
    // seam that can promise this, so the wrapper must be the outermost client.
    [Fact]
    public void Create_AlwaysWrapsWithModelRequestSettingsChatClient()
    {
        var factory = CreateFactory(("local", new ProviderEntry
        {
            Type = "ollama",
            Endpoint = "http://localhost:11434"
        }));

        var client = factory.Create(new ModelReference { Provider = "local", ModelId = "qwen3:30b" });

        Assert.IsType<ModelRequestSettingsChatClient>(client);
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

    // The CLI, doctor, and the daemon all use IProviderDescriptor.ResolveThinkingDialect (design D2, D6).
    [Theory]
    [InlineData("ollama_resolves_ollama_think", "ollama", null, ThinkingDialect.OllamaThink)]
    [InlineData("deepseek_resolves_deepseek", "deepseek", null, ThinkingDialect.DeepSeek)]
    [InlineData("compat_without_value_resolves_unresolved", "openai-compatible", null, ThinkingDialect.Unresolved)]
    [InlineData("compat_with_value_resolves_it", "openai-compatible", "ReasoningEffort", ThinkingDialect.ReasoningEffort)]
    [InlineData("compat_none_value_resolves_none", "openai-compatible", "None", ThinkingDialect.None)]
    [InlineData("anthropic_resolves_native", "anthropic", null, ThinkingDialect.Native)]
    [InlineData("openai_resolves_native", "openai", null, ThinkingDialect.Native)]
    [InlineData("openrouter_resolves_native", "openrouter", null, ThinkingDialect.Native)]
    [InlineData("venice_resolves_native", "veniceai", null, ThinkingDialect.Native)]
    [InlineData("copilot_resolves_native", "github-copilot", null, ThinkingDialect.Native)]
    public void Descriptor_resolves_thinking_dialect(string row, string type, string? configured, ThinkingDialect expected)
    {
        var entry = new ProviderEntry { Type = type };
        if (configured is not null)
            entry.SetVendorOptions(new JsonObject { ["ThinkingDialect"] = configured });

        var descriptor = ProviderDescriptors().Single(d => d.TypeKey == type);

        Assert.True(expected == descriptor.ResolveThinkingDialect(entry), row);
    }

    // Startup validation checks only the dialect, because the capability is resolved at runtime (D5).
    [Theory]
    [InlineData("startup_fails_on_hand_edited_unsupported_level", "None", ThinkingLevel.Low, true)]
    [InlineData("startup_fails_on_invalid_thinking_dialect", "Banana", ThinkingLevel.Off, true)]
    [InlineData("startup_accepts_level_that_the_dialect_sends", "ReasoningEffort", ThinkingLevel.Low, false)]
    public void Validate_checks_thinking_level_against_the_dialect(
        string row, string dialect, ThinkingLevel level, bool fails)
    {
        var entry = WireCapture.Entry("openai-compatible");
        entry.SetVendorOptions(new JsonObject { ["ThinkingDialect"] = dialect });
        var factory = WireCapture.Factory(new() { ["my-vllm"] = entry }, _ => new WireCapture().Handler);
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "my-vllm", ModelId = "qwen3", Thinking = level },
        };

        var error = factory.Validate(models);

        Assert.True(fails == error is not null, $"{row}: {error ?? "(no error)"}");
        if (!fails)
            return;
        Assert.Contains("my-vllm", error, StringComparison.Ordinal);
        Assert.Contains(dialect == "Banana" ? "ThinkingDialect" : level.ToString(), error, StringComparison.OrdinalIgnoreCase);
    }

    // The Ollama DisableThinking switch already forces think=false, so a Thinking value on the same
    // provider conflicts with it; startup rejects the pair instead of sending both.
    [Fact]
    public void startup_fails_on_thinking_with_ollama_disable_thinking()
    {
        var entry = WireCapture.Entry("ollama");
        entry.SetVendorOptions(new JsonObject { ["DisableThinking"] = true });
        var factory = WireCapture.Factory(new() { ["local"] = entry }, _ => new WireCapture().Handler);

        var error = factory.Validate(new ModelSelection
        {
            Main = new ModelReference { Provider = "local", ModelId = "qwen3:30b", Thinking = ThinkingLevel.Low },
        });

        Assert.NotNull(error);
        Assert.Contains("DisableThinking", error, StringComparison.Ordinal);
    }

    // Design D10: one Information line for each model client, with the inputs of the default limit.
    [Fact]
    public void startup_line_shows_limit_inputs_level_dialect_and_capability()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = new LoggerFactory([logs]);
        var factory = WireCapture.Factory(
            new() { ["my-openai"] = WireCapture.Entry("openai") }, _ => new WireCapture().Handler, loggerFactory);

        factory.Create(new ModelReference
        {
            Provider = "my-openai",
            ModelId = "gpt-5",
            ContextWindow = 131_072,
            Thinking = ThinkingLevel.Low,
        });

        var line = Assert.Single(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("gpt-5")).Message;
        Assert.Contains("maxOutputTokens=default 32000", line, StringComparison.Ordinal);
        Assert.Contains("window=131072", line, StringComparison.Ordinal);
        Assert.Contains("thinking=Low (configured)", line, StringComparison.Ordinal);
        Assert.Contains("dialect=Native", line, StringComparison.Ordinal);
        Assert.Contains("capability=", line, StringComparison.Ordinal);
    }

    private static IEnumerable<IProviderDescriptor> ProviderDescriptors()
    {
        var exchanger = GitHubCopilotProviderPluginTests.ExchangerReturning("copilot-token", apiBase: null);
        return
        [
            new OllamaDescriptor(SharedHttp),
            new OpenAiCompatibleDescriptor(SharedHttp),
            new DeepSeekDescriptor(SharedHttp),
            new AnthropicDescriptor(SharedHttp),
            new OpenAiDescriptor(SharedHttp),
            new OpenRouterDescriptor(SharedHttp),
            new VeniceAiDescriptor(SharedHttp),
            GitHubCopilotProviderPluginTests.DescriptorWithModels(exchanger),
        ];
    }

    private static ProviderPluginFactory CreateFactory(params (string name, ProviderEntry entry)[] providers)
    {
        var dict = new Dictionary<string, ProviderEntry>();
        foreach (var (name, entry) in providers)
            dict[name] = entry;
        return new ProviderPluginFactory(dict, AllPlugins, new FakeCapabilityResolver(null), NullLoggerFactory.Instance, TimeProvider.System);
    }
}
