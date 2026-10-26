// -----------------------------------------------------------------------
// <copyright file="RoutingChatClientProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Tests.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RoutingChatClientProviderTests
{
    [Fact]
    public void GetClient_caches_per_role_and_shares_Main_and_Fallback()
    {
        var provider = new RoutingChatClientProvider(
            new StubRouter(), NullNotificationSink.Instance, NullLoggerFactory.Instance);

        var main = provider.GetClient(ModelRole.Main);

        Assert.Same(main, provider.GetClient(ModelRole.Main));         // cached per role
        Assert.Same(main, provider.GetClient(ModelRole.Fallback));     // Fallback shares the Main routing client
        Assert.NotSame(main, provider.GetClient(ModelRole.Compaction));// Compaction is its own routing client
        Assert.Same(provider.GetClient(ModelRole.Compaction), provider.GetClient(ModelRole.Compaction));
    }

    // #2681: each candidate must get its own copy of the caller's options. Main is an Ollama client and
    // fails after its wrappers run; the fallback request must hold only the fallback's own fields.
    private sealed record FailoverRow(
        ModelReference Main,
        ProviderEntry MainEntry,
        ModelReference Fallback,
        ProviderEntry FallbackEntry,
        bool FallbackSucceeds,
        Action<JsonElement> ExpectFallbackBody);

    private static ModelReference Definition(string provider, int? limit = null, ThinkingLevel? thinking = null) => new()
    {
        Provider = provider,
        ModelId = WireCapture.ModelIdFor(WireCapture.Entry(provider).Type),
        ContextWindow = 131_072,
        MaxOutputTokens = limit,
        Thinking = thinking,
    };

    private static ProviderEntry OllamaWithThinkingDisabled()
    {
        var entry = WireCapture.Entry("ollama");
        entry.SetVendorOptions(new JsonObject { ["DisableThinking"] = true });
        return entry;
    }

    private static readonly Dictionary<string, FailoverRow> FailoverRows = new()
    {
        ["fallback_receives_no_dialect_field_from_failed_candidate"] = new(
            Definition("ollama", thinking: ThinkingLevel.Off), WireCapture.Entry("ollama"),
            Definition("deepseek", thinking: ThinkingLevel.High), WireCapture.Entry("deepseek"),
            FallbackSucceeds: true,
            body =>
            {
                Assert.False(body.TryGetProperty("think", out _), "the Ollama think field leaked to the fallback");
                Assert.Equal("enabled", WireCapture.Text(body, "thinking", "type"));
                Assert.Equal("high", WireCapture.Text(body, "reasoning_effort"));
            }),
        ["fallback_receives_no_vendor_option_field_from_failed_candidate"] = new(
            Definition("ollama"), OllamaWithThinkingDisabled(),
            Definition("openai-compatible"), WireCapture.Entry("openai-compatible"),
            FallbackSucceeds: true,
            body => Assert.False(body.TryGetProperty("think", out _), "the Ollama vendor option leaked to the fallback")),
        ["fallback_applies_its_own_definition_settings"] = new(
            Definition("ollama", limit: 8_000), WireCapture.Entry("ollama"),
            Definition("deepseek", limit: 16_000, thinking: ThinkingLevel.Max), WireCapture.Entry("deepseek"),
            FallbackSucceeds: true,
            body =>
            {
                Assert.Equal("16000", WireCapture.Text(body, "max_tokens"));
                Assert.Equal("max", WireCapture.Text(body, "reasoning_effort"));
            }),
        ["caller_options_unchanged_after_all_candidates"] = new(
            Definition("ollama", limit: 8_000, thinking: ThinkingLevel.Off), WireCapture.Entry("ollama"),
            Definition("deepseek", limit: 16_000, thinking: ThinkingLevel.High), WireCapture.Entry("deepseek"),
            FallbackSucceeds: false,
            body => Assert.Equal("16000", WireCapture.Text(body, "max_tokens"))),
    };

    public static TheoryData<string> FailoverRowNames => new(FailoverRows.Keys);

    [Theory]
    [MemberData(nameof(FailoverRowNames))]
    public async Task Failover_isolates_candidate_options(string row)
    {
        var spec = FailoverRows[row];
        var mainCapture = new WireCapture(HttpStatusCode.InternalServerError);
        var fallbackCapture = spec.FallbackSucceeds
            ? new WireCapture(HttpStatusCode.OK, CompatChatResponse)
            : new WireCapture(HttpStatusCode.BadRequest);
        var mainType = spec.MainEntry.Type;
        var factory = WireCapture.Factory(
            new() { [spec.Main.Provider] = spec.MainEntry, [spec.Fallback.Provider] = spec.FallbackEntry },
            type => type == mainType ? mainCapture.Handler : fallbackCapture.Handler);
        var routing = new RoutingChatClient(
            new StubRouter(factory.Create(spec.Main), factory.Create(spec.Fallback)),
            new ChatRoutingContext { Role = ModelRole.Main },
            NullNotificationSink.Instance,
            NullLogger.Instance,
            TimeProvider.System);
        var callerOptions = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary() };

        var error = await Record.ExceptionAsync(() => routing.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], callerOptions, TestContext.Current.CancellationToken));

        Assert.Equal(!spec.FallbackSucceeds, error is not null);
        Assert.Single(mainCapture.Bodies);
        spec.ExpectFallbackBody(fallbackCapture.SingleBody());
        // The caller's object is never a candidate's working copy.
        Assert.Empty(callerOptions.AdditionalProperties);
        Assert.Null(callerOptions.MaxOutputTokens);
        Assert.Null(callerOptions.Reasoning);
    }

    private const string CompatChatResponse = """
        {"id":"response-1","model":"m","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}
        """;

    private sealed class StubRouter(params IChatClient[] candidates) : IChatClientRouter
    {
        private readonly IReadOnlyList<IChatClient> _candidates = candidates.Length == 0 ? [new FakeChatClient()] : candidates;
        public IReadOnlyList<IChatClient> Route(ChatRoutingContext context) => _candidates;
    }

    [Fact]
    public async Task Compaction_role_call_fails_over_to_fallback_when_primary_refuses_connection()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "p", ModelId = "main" },
            Fallback = new ModelReference { Provider = "p", ModelId = "fb" },
            Compaction = new ModelReference { Provider = "p", ModelId = "comp" }
        };
        var router = new RoleBasedFailoverRouter(r => r.ModelId == "fb"
            ? new FakeChatClient((_, _, _) =>
                Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "fallback")])))
            : new FakeChatClient((_, _, _) => throw new HttpRequestException("connection refused")), models);
        var provider = new RoutingChatClientProvider(
            router, NullNotificationSink.Instance, NullLoggerFactory.Instance);

        var response = await provider.GetClient(ModelRole.Compaction)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fallback", response.Messages[0].Text);
    }
}
