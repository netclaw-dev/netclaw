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
/// Precedence of design D8, the self-hosted clamp of D4, and the per-call debug line of D10. Each row
/// goes through the real <see cref="Netclaw.Daemon.Configuration.ProviderPluginFactory"/> stack of a
/// self-hosted OpenAI-compatible provider and reads the request body. The probe of that endpoint
/// (a fake capability resolver) knows the window when a row says so. Every row also proves the shared
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

    private static ModelReference Model(int? limit = null, ThinkingLevel? thinking = null) => new()
    {
        Provider = "local",
        ModelId = "qwen3",
        MaxOutputTokens = limit,
        Thinking = thinking,
    };

    private static ChatOptions Suppressed(int? limit = null) => new()
    {
        MaxOutputTokens = limit,
        AdditionalProperties = new() { [NetclawChatOptionKeys.SuppressReasoning] = true },
    };

    // About 15000 tokens of tool schema text: the clamp must count it.
    private static ChatOptions WithLargeTool() => new()
    {
        Tools = [AIFunctionFactory.Create(() => "ok", "large_tool", new string('d', 60_000))],
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
            ProbeWindow: 131_072),
        ["suppression_wins_over_model_level"] = new(Model(thinking: ThinkingLevel.High), ThinkingDialect.DeepSeek,
            () => Suppressed(),
            b =>
            {
                Assert.Equal("disabled", WireCapture.Text(b, "thinking", "type"));
                Assert.Null(WireCapture.Text(b, "reasoning_effort"));
            }),
        // Window 40960, prompt 40000 tokens (160000 characters / 4): the headroom is below the floor.
        ["debug_line_shows_default_clamped_and_prompt_estimate"] = new(Model(), ThinkingDialect.ReasoningEffort,
            () => new ChatOptions(),
            b => Assert.Equal("1024", WireCapture.Text(b, "max_tokens")),
            ProbeWindow: 40_960,
            PromptChars: 160_000,
            DebugLinePatterns: [@"maxOutputTokens=1,?024 \(default-clamped\)", @"promptEstimate=40,?000"],
            ExpectWarning: true),
        ["debug_line_shows_call_source_for_curation_limit"] = new(Model(limit: 32_000, thinking: ThinkingLevel.Low), ThinkingDialect.ReasoningEffort,
            () => Suppressed(limit: 4_096),
            b =>
            {
                Assert.Equal("4096", WireCapture.Text(b, "max_tokens"));
                Assert.Equal("none", WireCapture.Text(b, "reasoning_effort"));
            },
            DebugLinePatterns: [@"maxOutputTokens=4,?096 \(call\)", @"thinking=Off \(suppressed\)", @"dialect=ReasoningEffort"]),
        // 40960 - 30000 - 2048 = 8912
        ["prompt_without_tools_sends_8912"] = new(Model(), ThinkingDialect.ReasoningEffort,
            () => new ChatOptions(),
            b => Assert.Equal("8912", WireCapture.Text(b, "max_tokens")),
            ProbeWindow: 40_960,
            PromptChars: 120_000),
        // The same prompt plus about 15000 tokens of tool schemas leaves less than the floor.
        ["clamp_counts_tool_schemas"] = new(Model(), ThinkingDialect.ReasoningEffort,
            WithLargeTool,
            b => Assert.Equal("1024", WireCapture.Text(b, "max_tokens")),
            ProbeWindow: 40_960,
            PromptChars: 120_000,
            ExpectWarning: true),
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
