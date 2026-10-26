// -----------------------------------------------------------------------
// <copyright file="ProviderRequestMappingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Tests.Providers.GitHubCopilot;
using Netclaw.Providers;
using Netclaw.Providers.Anthropic;
using Netclaw.Providers.DeepSeek;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OpenRouter;
using Netclaw.Providers.SelfHosted;
using Netclaw.Providers.VeniceAi;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// Design D3: the neutral settings of a definition reach each provider as that provider's own request
/// fields. Each row builds the real <see cref="ProviderPluginFactory"/> stack and reads the request body
/// that the SDK sends. A capture handler answers with an error, so no row uses the network.
/// </summary>
public sealed class ProviderRequestMappingTests
{
    private sealed record Row(
        string ProviderType,
        Action<JsonElement> Expect,
        int? Limit = null,
        ThinkingLevel? Thinking = null,
        string? VendorOptions = null,
        int? Window = 200_000,
        ResolvedModelCapabilities? Capability = null,
        int PromptChars = 5);

    // Aaron's default-limit rule (review of PR #2694): a hosted API gets min(32000, its own known
    // model max) and no window clamp; a self-hosted server gets the window clamp only when the probe
    // of that endpoint knows the window, and no limit field at all when it does not.
    private static ResolvedModelCapabilities Probe(int? window = null, int? modelMax = null) =>
        new("m", null, null, window) { MaxOutputTokens = modelMax };

    private static readonly Dictionary<string, Row> Rows = new()
    {
        // The Anthropic SDK sends 1024 when no limit is set; Netclaw sends the D4 default instead.
        ["anthropic_unset_limit_sends_clamped_default"] = new("anthropic",
            b => Assert.Equal("32000", WireCapture.Text(b, "max_tokens"))),
        ["anthropic_limit_64000_sends_64000"] = new("anthropic",
            b => Assert.Equal("64000", WireCapture.Text(b, "max_tokens")), Limit: 64_000),
        ["anthropic_low_sends_adaptive_and_effort_low"] = new("anthropic",
            b =>
            {
                Assert.Equal("adaptive", WireCapture.Text(b, "thinking", "type"));
                Assert.Equal("low", WireCapture.Text(b, "output_config", "effort"));
            }, Thinking: ThinkingLevel.Low),
        ["anthropic_max_sends_effort_max"] = new("anthropic",
            b =>
            {
                Assert.Equal("adaptive", WireCapture.Text(b, "thinking", "type"));
                Assert.Equal("max", WireCapture.Text(b, "output_config", "effort"));
            }, Thinking: ThinkingLevel.Max),
        ["anthropic_unset_level_sends_no_thinking"] = new("anthropic",
            b =>
            {
                Assert.False(b.TryGetProperty("thinking", out _));
                Assert.Null(WireCapture.Text(b, "output_config", "effort"));
            }),

        ["openai_responses_limit_sends_max_output_tokens"] = new("openai",
            b => Assert.Equal("8000", WireCapture.Text(b, "max_output_tokens")), Limit: 8_000),
        ["openai_responses_low_sends_reasoning_effort_low"] = new("openai",
            b => Assert.Equal("low", WireCapture.Text(b, "reasoning", "effort")), Thinking: ThinkingLevel.Low),

        ["copilot_responses_low_sends_reasoning_effort_low"] = new("github-copilot",
            b => Assert.Equal("low", WireCapture.Text(b, "reasoning", "effort")), Thinking: ThinkingLevel.Low),

        ["openrouter_high_keeps_exclude_and_sets_effort"] = new("openrouter",
            b =>
            {
                Assert.Equal("high", WireCapture.Text(b, "reasoning", "effort"));
                Assert.Equal("true", WireCapture.Text(b, "reasoning", "exclude"));
            }, Thinking: ThinkingLevel.High, VendorOptions: """{"ExcludeReasoning":true}"""),
        ["openrouter_limit_sends_max_completion_tokens"] = new("openrouter",
            b => Assert.Equal("8000", WireCapture.Text(b, "max_completion_tokens")), Limit: 8_000),

        ["venice_max_sends_reasoning_effort_max"] = new("veniceai",
            b => Assert.Equal("max", WireCapture.Text(b, "reasoning_effort")), Thinking: ThinkingLevel.Max),
        ["venice_limit_sends_max_completion_tokens"] = new("veniceai",
            b => Assert.Equal("8000", WireCapture.Text(b, "max_completion_tokens")), Limit: 8_000),

        ["deepseek_max_sends_thinking_enabled_and_max"] = new("deepseek",
            b =>
            {
                Assert.Equal("enabled", WireCapture.Text(b, "thinking", "type"));
                Assert.Equal("max", WireCapture.Text(b, "reasoning_effort"));
            }, Thinking: ThinkingLevel.Max),

        ["ollama_limit_sends_num_predict"] = new("ollama",
            b => Assert.Equal("8000", WireCapture.Text(b, "options", "num_predict")), Limit: 8_000),
        ["ollama_off_sends_think_false"] = new("ollama",
            b => Assert.Equal("false", WireCapture.Text(b, "think")), Thinking: ThinkingLevel.Off),
        ["ollama_low_sends_think_low"] = new("ollama",
            b => Assert.Equal("low", WireCapture.Text(b, "think")), Thinking: ThinkingLevel.Low),

        ["compat_limit_sends_max_tokens"] = new("openai-compatible",
            b => Assert.Equal("8000", WireCapture.Text(b, "max_tokens")), Limit: 8_000),
        ["compat_reasoning_effort_off_sends_none"] = new("openai-compatible",
            b => Assert.Equal("none", WireCapture.Text(b, "reasoning_effort")),
            Thinking: ThinkingLevel.Off, VendorOptions: """{"ThinkingDialect":"ReasoningEffort"}"""),
        ["compat_chat_template_off_sends_enable_thinking_false"] = new("openai-compatible",
            b => Assert.Equal("false", WireCapture.Text(b, "chat_template_kwargs", "enable_thinking")),
            Thinking: ThinkingLevel.Off, VendorOptions: """{"ThinkingDialect":"ChatTemplateKwargs"}"""),

        ["compat_unknown_window_sends_no_max_tokens"] = new("openai-compatible",
            b => Assert.Null(WireCapture.Text(b, "max_tokens")), Window: null),
        // 131072 - 110000 - 6553 = 14519
        ["compat_probe_window_clamps_default"] = new("openai-compatible",
            b => Assert.Equal("14519", WireCapture.Text(b, "max_tokens")),
            Window: null, Capability: Probe(window: 131_072), PromptChars: 440_000),
        // A catalog maximum describes another host, so it does not cap a self-hosted server.
        ["compat_catalog_max_does_not_cap_self_hosted"] = new("openai-compatible",
            b => Assert.Equal("32000", WireCapture.Text(b, "max_tokens")),
            Window: null, Capability: Probe(window: 131_072, modelMax: 16_384)),
        // A hosted model with an unknown window and an unknown model max gets the full default.
        ["hosted_unknown_window_and_max_sends_32000"] = new("openai",
            b => Assert.Equal("32000", WireCapture.Text(b, "max_output_tokens")),
            Window: null, PromptChars: 440_000),
        // A hosted API rejects input + max_tokens above the window (Anthropic before Claude 4.5), so a
        // catalog window clamps a hosted default too: 200000 - 170000 - 10000 = 20000.
        ["hosted_catalog_window_clamps_default"] = new("anthropic",
            b => Assert.Equal("20000", WireCapture.Text(b, "max_tokens")),
            Window: null, Capability: Probe(window: 200_000), PromptChars: 680_000),
        ["hosted_known_model_max_caps_default"] = new("anthropic",
            b => Assert.Equal("16384", WireCapture.Text(b, "max_tokens")),
            Capability: Probe(modelMax: 16_384)),
    };

    public static TheoryData<string> RowNames => new(Rows.Keys);

    [Theory]
    [MemberData(nameof(RowNames))]
    public async Task Maps_settings_to_wire(string row)
    {
        var spec = Rows[row];
        var capture = new WireCapture();
        var entry = WireCapture.Entry(spec.ProviderType);
        if (spec.VendorOptions is { } vendorOptions)
            entry.SetVendorOptions(JsonNode.Parse(vendorOptions)!.AsObject());
        var factory = WireCapture.Factory(
            new() { ["p"] = entry }, _ => capture.Handler, capabilities: new FakeCapabilityResolver(spec.Capability));
        var client = factory.Create(new ModelReference
        {
            Provider = "p",
            ModelId = WireCapture.ModelIdFor(spec.ProviderType),
            ContextWindow = spec.Window,
            MaxOutputTokens = spec.Limit,
            Thinking = spec.Thinking,
        });

        // The capture handler answers 400, so the SDK throws after it sends the request.
        var error = await Record.ExceptionAsync(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, new string('x', spec.PromptChars))],
            cancellationToken: TestContext.Current.CancellationToken));

        spec.Expect(capture.SingleBody(error));
    }

    // A lookup with no result is cached for about 10 minutes: within that time a call does not ask
    // again, after it a call asks again. Only the first miss logs the "capability resolved" line.
    [Fact]
    public async Task capability_miss_is_cached_for_a_short_time()
    {
        var capture = new WireCapture();
        var capabilities = new FakeCapabilityResolver(null);
        var time = new FakeTimeProvider();
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = new LoggerFactory([logs]);
        var factory = WireCapture.Factory(
            new() { ["p"] = WireCapture.Entry("openai") }, _ => capture.Handler, loggerFactory, capabilities, time);
        var client = factory.Create(new ModelReference { Provider = "p", ModelId = "gpt-5" });

        async Task CallAsync() => await Record.ExceptionAsync(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken));

        await CallAsync();
        await CallAsync();
        Assert.Equal(1, capabilities.Calls);

        time.Advance(TimeSpan.FromMinutes(11));
        await CallAsync();
        Assert.Equal(2, capabilities.Calls);

        Assert.Single(logs.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("capability resolved", StringComparison.Ordinal));
    }

    // A failed capability lookup is a normal network condition, so it is not cached for the client's
    // lifetime: a later call (after the short miss cache) looks the capability up again and then
    // uses the model max.
    [Fact]
    public async Task failed_capability_lookup_is_retried_on_a_later_call()
    {
        var capture = new WireCapture();
        var capabilities = new FakeCapabilityResolver(new ResolvedModelCapabilities("gpt-5", null, null) { MaxOutputTokens = 16_384 })
        {
            FailuresBeforeResult = 1,
        };
        var time = new FakeTimeProvider();
        var factory = WireCapture.Factory(
            new() { ["p"] = WireCapture.Entry("openai") }, _ => capture.Handler, capabilities: capabilities, timeProvider: time);
        var client = factory.Create(new ModelReference { Provider = "p", ModelId = "gpt-5" });

        for (var call = 0; call < 2; call++)
        {
            await Record.ExceptionAsync(() => client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken));
            time.Advance(TimeSpan.FromMinutes(11));
        }

        Assert.Equal(2, capabilities.Calls);
        var bodies = capture.Bodies.Select(body => JsonDocument.Parse(body).RootElement).ToList();
        Assert.Equal(["32000", "16384"], bodies.Select(body => WireCapture.Text(body, "max_output_tokens")));
    }
}

/// <summary>
/// Builds plugins whose model clients send every request to a capture handler, and records each
/// request body. Shared by the wire, failover, and factory tests.
/// </summary>
internal sealed class WireCapture
{
    private readonly List<string> _bodies = [];

    public WireCapture(HttpStatusCode status = HttpStatusCode.BadRequest, string? responseJson = null)
    {
        Handler = new FakeHttpMessageHandler(request =>
        {
            lock (_bodies)
                _bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    responseJson ?? """{"error":{"type":"invalid_request_error","message":"captured"}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
    }

    public HttpMessageHandler Handler { get; }

    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_bodies)
                return [.. _bodies];
        }
    }

    /// <summary>The one captured body. <paramref name="callError"/> explains a call that sent nothing.</summary>
    public JsonElement SingleBody(Exception? callError = null)
    {
        var bodies = Bodies;
        Assert.True(bodies.Count == 1, $"expected one request, got {bodies.Count}; call error: {callError?.Message ?? "none"}");
        return JsonDocument.Parse(bodies[0]).RootElement.Clone();
    }

    /// <summary>The value at <paramref name="path"/> as text (raw JSON for a non-string), or null when absent.</summary>
    public static string? Text(JsonElement body, params string[] path)
    {
        var current = body;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
    }

    public static ProviderEntry Entry(string type) => type switch
    {
        "ollama" => new ProviderEntry { Type = type, Endpoint = "http://wire.test" },
        "openai-compatible" => new ProviderEntry { Type = type, Endpoint = "http://wire.test/v1" },
        "github-copilot" => new ProviderEntry
        {
            Type = type,
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString("oauth-1"),
        },
        _ => new ProviderEntry { Type = type, ApiKey = new SensitiveString("test-key") },
    };

    public static string ModelIdFor(string type) => type switch
    {
        "anthropic" => "claude-opus-5-5",
        "openai" => "gpt-5",
        "github-copilot" => "gpt-4o",
        "openrouter" => "openai/gpt-5",
        "veniceai" => "venice-uncensored",
        "deepseek" => "deepseek-v4-pro",
        "ollama" => "qwen3:30b",
        _ => "qwen3",
    };

    public static ProviderPluginFactory Factory(
        Dictionary<string, ProviderEntry> providers,
        Func<string, HttpMessageHandler> handlerFor,
        ILoggerFactory? loggerFactory = null,
        IModelCapabilityResolver? capabilities = null,
        TimeProvider? timeProvider = null)
    {
        var http = new HttpClient();
        var exchanger = GitHubCopilotProviderPluginTests.ExchangerReturning(
            "copilot-token", apiBase: "https://api.githubcopilot.com");
        ILlmProviderPlugin[] plugins =
        [
            new AnthropicProviderPlugin(new AnthropicDescriptor(http)) { HttpHandlerOverride = handlerFor("anthropic") },
            new OpenAiProviderPlugin(new OpenAiDescriptor(http)) { HttpHandlerOverride = handlerFor("openai") },
            new OpenRouterProviderPlugin(new OpenRouterDescriptor(http)) { HttpHandlerOverride = handlerFor("openrouter") },
            new VeniceAiProviderPlugin(new VeniceAiDescriptor(http)) { HttpHandlerOverride = handlerFor("veniceai") },
            new DeepSeekProviderPlugin(new DeepSeekDescriptor(http), NullLoggerFactory.Instance)
            {
                HttpHandlerOverride = handlerFor("deepseek"),
            },
            new OllamaProviderPlugin(new OllamaDescriptor(http)) { HttpHandlerOverride = handlerFor("ollama") },
            new OpenAiCompatibleProviderPlugin(new OpenAiCompatibleDescriptor(http), NullLoggerFactory.Instance)
            {
                HttpHandlerOverride = handlerFor("openai-compatible"),
            },
            new GitHubCopilotProviderPlugin(
                GitHubCopilotProviderPluginTests.DescriptorWithModels(exchanger, "/responses"), exchanger)
            {
                TransportOverride = new HttpClientPipelineTransport(new HttpClient(handlerFor("github-copilot"))),
            },
        ];

        return new ProviderPluginFactory(
            providers,
            plugins,
            capabilities ?? new FakeCapabilityResolver(null),
            loggerFactory ?? NullLoggerFactory.Instance,
            timeProvider ?? TimeProvider.System);
    }
}
