// -----------------------------------------------------------------------
// <copyright file="ModelRequestSettingsChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Tests.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Precedence of design D8 and the per-call debug line of D10: a call-site value wins, the model value
/// fills an unset field, and the clamped default fills the rest. Every row also proves the shared
/// post-conditions: the inner client gets its own copy of the options (D9), the suppression intent key
/// never reaches it, and the caller's options do not change.
/// </summary>
public sealed class ModelRequestSettingsChatClientTests
{
    private sealed record Row(
        ModelReference Model,
        ThinkingDialect Dialect,
        Func<ChatOptions?> CallerOptions,
        Action<ChatOptions> Expect,
        int PromptChars = 20,
        bool Streaming = false,
        string[]? DebugLinePatterns = null,
        bool ExpectWarning = false);

    private static ModelReference Model(int? limit = null, ThinkingLevel? thinking = null, int? window = 131_072) => new()
    {
        Provider = "p",
        ModelId = "m",
        ContextWindow = window,
        MaxOutputTokens = limit,
        Thinking = thinking,
    };

    private static ChatOptions Suppressed(int? limit = null) => new()
    {
        MaxOutputTokens = limit,
        AdditionalProperties = new() { [NetclawChatOptionKeys.SuppressReasoning] = true },
    };

    private static readonly Dictionary<string, Row> Rows = new()
    {
        // Memory curation sizes its JSON reply to 4096 tokens.
        ["call_site_limit_wins_over_model"] = new(Model(limit: 32_000), ThinkingDialect.Native,
            () => new ChatOptions { MaxOutputTokens = 4_096 },
            o => Assert.Equal(4_096, o.MaxOutputTokens)),
        ["model_limit_fills_unset_call_limit"] = new(Model(limit: 32_000), ThinkingDialect.Native,
            () => new ChatOptions(),
            o => Assert.Equal(32_000, o.MaxOutputTokens),
            Streaming: true),
        ["default_fills_unset_model_limit"] = new(Model(), ThinkingDialect.Native,
            () => null,
            o => Assert.Equal(32_000, o.MaxOutputTokens)),
        ["suppression_wins_over_model_level"] = new(Model(thinking: ThinkingLevel.High), ThinkingDialect.DeepSeek,
            () => Suppressed(),
            o =>
            {
                var body = Body(o);
                Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
                Assert.False(body.TryGetProperty("reasoning_effort", out _), "suppression sends no effort");
            }),
        // Window 40960, prompt 40000 tokens (160000 characters / 4): the headroom is below the floor.
        ["debug_line_shows_default_clamped_and_prompt_estimate"] = new(Model(window: 40_960), ThinkingDialect.Native,
            () => new ChatOptions(),
            o => Assert.Equal(1_024, o.MaxOutputTokens),
            PromptChars: 160_000,
            DebugLinePatterns: [@"maxOutputTokens=1,?024 \(default-clamped\)", @"promptEstimate=40,?000"],
            ExpectWarning: true),
        ["debug_line_shows_call_source_for_curation_limit"] = new(Model(limit: 32_000, thinking: ThinkingLevel.Low), ThinkingDialect.ReasoningEffort,
            () => Suppressed(limit: 4_096),
            o =>
            {
                Assert.Equal(4_096, o.MaxOutputTokens);
                Assert.Equal("none", Body(o).GetProperty("reasoning_effort").GetString());
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
        ChatOptions? forwarded = null;
        var inner = new FakeChatClient(
            (_, opts, _) =>
            {
                forwarded = opts;
                return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
            },
            streamHandler: (_, opts, _) =>
            {
                forwarded = opts;
                return StreamOne();
            });
        var logs = new CapturingLoggerProvider();
        var client = new ModelRequestSettingsChatClient(
            inner, spec.Model, spec.Dialect, new FakeCapabilityResolver(null), logs.CreateLogger("model-settings"));
        ChatMessage[] messages = [new(ChatRole.User, new string('x', spec.PromptChars))];

        if (spec.Streaming)
        {
            await foreach (var _ in client.GetStreamingResponseAsync(messages, callerOptions, TestContext.Current.CancellationToken))
            {
            }
        }
        else
        {
            await client.GetResponseAsync(messages, callerOptions, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(forwarded);
        Assert.NotSame(callerOptions, forwarded);
        Assert.False(forwarded.AdditionalProperties?.ContainsKey(NetclawChatOptionKeys.SuppressReasoning) ?? false,
            "the suppression intent key must never reach the provider client");
        Assert.Equal(callerBefore, Snapshot(callerOptions));
        spec.Expect(forwarded);

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
        });

    private static JsonElement Body(ChatOptions options)
        => JsonSerializer.SerializeToElement(options.AdditionalProperties ?? new AdditionalPropertiesDictionary());

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamOne()
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
    }
}
