// -----------------------------------------------------------------------
// <copyright file="ModelRequestSettingsChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Tests.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Precedence of design D8, the self-hosted window cap, and the per-call debug line of D10. Each row
/// goes through the real <see cref="Netclaw.Daemon.Configuration.ProviderPluginFactory"/> stack of a
/// self-hosted OpenAI-compatible provider and reads the request body. The probe of that endpoint
/// (a fake capability resolver) knows the window when a row says so. The prompt never changes the
/// limit. Every row also proves the shared
/// post-conditions: the suppression intent key never reaches the wire, and the caller's options do not
/// change.
/// </summary>
public sealed class ModelRequestSettingsChatClientTests
{
    private sealed record Row(
        ModelReference Model,
        ThinkingDialect Dialect,
        Func<ChatOptions?> CallerOptions,
        Action<JsonElement> Expect,
        int? ProbeWindow = null,
        int PromptChars = 20,
        bool Streaming = false,
        string[]? DebugLinePatterns = null,
        bool ExpectWarning = false);

    private static ModelReference Model(int? limit = null, ThinkingLevel? thinking = null, int? window = null) => new()
    {
        Provider = "local",
        ModelId = "qwen3",
        MaxOutputTokens = limit,
        Thinking = thinking,
        ContextWindow = window,
    };

    private static ChatOptions Suppressed(int? limit = null) => new()
    {
        MaxOutputTokens = limit,
        AdditionalProperties = new() { [NetclawChatOptionKeys.SuppressReasoning] = true },
    };

    private static readonly Dictionary<string, Row> Rows = new()
    {
        // Memory curation sizes its JSON reply to 4096 tokens.
        ["call_site_limit_wins_over_model"] = new(Model(limit: 32_000), ThinkingDialect.ReasoningEffort,
            () => new ChatOptions { MaxOutputTokens = 4_096 },
            b => Assert.Equal("4096", WireCapture.Text(b, "max_tokens"))),
        ["model_limit_fills_unset_call_limit"] = new(Model(limit: 32_000), ThinkingDialect.ReasoningEffort,
            () => new ChatOptions(),
            b => Assert.Equal("32000", WireCapture.Text(b, "max_tokens")),
            Streaming: true),
        ["default_fills_unset_model_limit"] = new(Model(), ThinkingDialect.ReasoningEffort,
            () => null,
            b => Assert.Equal("32000", WireCapture.Text(b, "max_tokens")),
            ProbeWindow: 131_072,
            DebugLinePatterns: [@"maxOutputTokens=32,?000 \(default\)"]),
        ["unknown_window_sends_no_limit"] = new(Model(), ThinkingDialect.ReasoningEffort,
            () => null,
            b => Assert.Null(WireCapture.Text(b, "max_tokens")),
            DebugLinePatterns: [@"maxOutputTokens=none \(window unknown\)"]),
        ["suppression_wins_over_model_level"] = new(Model(thinking: ThinkingLevel.High), ThinkingDialect.DeepSeek,
            () => Suppressed(),
            b =>
            {
                Assert.Equal("disabled", WireCapture.Text(b, "thinking", "type"));
                Assert.Null(WireCapture.Text(b, "reasoning_effort"));
            }),
        // A prompt near the window no longer shrinks the reply or warns: the cap is a static quarter of
        // the window (40960 / 4 = 10240), whatever the prompt.
        ["prompt_near_probe_window_keeps_window_cap"] = new(Model(), ThinkingDialect.ReasoningEffort,
            () => new ChatOptions(),
            b => Assert.Equal("10240", WireCapture.Text(b, "max_tokens")),
            ProbeWindow: 40_960,
            PromptChars: 160_000,
            DebugLinePatterns: [@"maxOutputTokens=10,?240 \(default-clamped\)"]),
        ["configured_window_caps_default"] = new(Model(window: 40_960), ThinkingDialect.ReasoningEffort,
            () => new ChatOptions(),
            b => Assert.Equal("10240", WireCapture.Text(b, "max_tokens"))),
        ["prompt_past_configured_window_keeps_configured_limit"] = new(Model(limit: 8_192, window: 40_960), ThinkingDialect.ReasoningEffort,
            () => new ChatOptions(),
            b => Assert.Equal("8192", WireCapture.Text(b, "max_tokens")),
            PromptChars: 160_000),
        ["debug_line_shows_call_source_for_curation_limit"] = new(Model(limit: 32_000, thinking: ThinkingLevel.Low), ThinkingDialect.ReasoningEffort,
            () => Suppressed(limit: 4_096),
            b =>
            {
                Assert.Equal("4096", WireCapture.Text(b, "max_tokens"));
                Assert.Equal("none", WireCapture.Text(b, "reasoning_effort"));
            },
            DebugLinePatterns: [@"maxOutputTokens=4,?096 \(call\)", @"thinking=Off \(suppressed\)", @"dialect=ReasoningEffort"]),
    };

    public static TheoryData<string> RowNames => new(Rows.Keys);

    [Theory]
    [MemberData(nameof(RowNames))]
    public async Task Applies_model_settings(string row)
    {
        var spec = Rows[row];
        var callerOptions = spec.CallerOptions();
        var callerBefore = Snapshot(callerOptions);
        var capture = new WireCapture();
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = new LoggerFactory([logs]);
        var entry = WireCapture.Entry("openai-compatible");
        entry.SetVendorOptions(new JsonObject { ["ThinkingDialect"] = spec.Dialect.ToString() });
        var probe = spec.ProbeWindow is { } window ? new ResolvedModelCapabilities("qwen3", null, null, window) : null;
        var factory = WireCapture.Factory(
            new() { ["local"] = entry }, _ => capture.Handler, loggerFactory, new FakeCapabilityResolver(probe));
        var client = factory.Create(spec.Model);
        ChatMessage[] messages = [new(ChatRole.User, new string('x', spec.PromptChars))];

        // The capture handler answers 400, so the client throws after it sends the request.
        var error = spec.Streaming
            ? await Record.ExceptionAsync(async () =>
            {
                await foreach (var _ in client.GetStreamingResponseAsync(messages, callerOptions, TestContext.Current.CancellationToken))
                {
                }
            })
            : await Record.ExceptionAsync(() => client.GetResponseAsync(messages, callerOptions, TestContext.Current.CancellationToken));

        var body = capture.SingleBody(error);
        Assert.Null(WireCapture.Text(body, NetclawChatOptionKeys.SuppressReasoning));
        Assert.Equal(callerBefore, Snapshot(callerOptions));
        spec.Expect(body);

        var debugLines = logs.Entries.Where(e => e.Level == LogLevel.Debug).Select(e => e.Message).ToList();
        foreach (var pattern in spec.DebugLinePatterns ?? [])
            Assert.Contains(debugLines, line => Regex.IsMatch(line, pattern));
        Assert.Equal(spec.ExpectWarning, logs.Entries.Any(e => e.Level == LogLevel.Warning));
    }

    private static string Snapshot(ChatOptions? options) => options is null
        ? "null"
        : JsonSerializer.Serialize(new
        {
            options.MaxOutputTokens,
            Effort = options.Reasoning?.Effort,
            Properties = options.AdditionalProperties,
            Tools = options.Tools?.Count,
        });
}
