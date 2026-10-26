// -----------------------------------------------------------------------
// <copyright file="ThinkingDialectMapTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// The dialect table of design D2 and D3: provider dialect x level -> request fields. A body-field
/// dialect writes <see cref="ChatOptions.AdditionalProperties"/>; the Native dialect and the Ollama
/// levels write <see cref="ChatOptions.Reasoning"/>. The rows read the body fields as JSON, so they do
/// not depend on the dictionary type that the map uses.
/// </summary>
public sealed class ThinkingDialectMapTests
{
    private sealed record Row(
        ThinkingDialect Dialect,
        ThinkingLevel? Level,
        bool Suppress,
        Action<ChatOptions> Expect,
        string? UnsupportedLevels = null);

    private static readonly Dictionary<string, Row> Rows = new()
    {
        ["native_off_sets_effort_none"] = new(ThinkingDialect.Native, ThinkingLevel.Off, false,
            o => ExpectEffort(o, ReasoningEffort.None)),
        ["native_low_sets_effort_low"] = new(ThinkingDialect.Native, ThinkingLevel.Low, false,
            o => ExpectEffort(o, ReasoningEffort.Low)),
        ["native_max_sets_effort_extra_high"] = new(ThinkingDialect.Native, ThinkingLevel.Max, false,
            o => ExpectEffort(o, ReasoningEffort.ExtraHigh)),

        ["deepseek_off_sets_thinking_disabled"] = new(ThinkingDialect.DeepSeek, ThinkingLevel.Off, false,
            o => ExpectBody(o, """{"thinking":{"type":"disabled"}}""")),
        ["deepseek_low_sets_enabled_and_low"] = new(ThinkingDialect.DeepSeek, ThinkingLevel.Low, false,
            o => ExpectBody(o, """{"thinking":{"type":"enabled"},"reasoning_effort":"low"}""")),
        ["deepseek_high_sets_enabled_and_high"] = new(ThinkingDialect.DeepSeek, ThinkingLevel.High, false,
            o => ExpectBody(o, """{"thinking":{"type":"enabled"},"reasoning_effort":"high"}""")),
        ["deepseek_max_sets_enabled_and_max"] = new(ThinkingDialect.DeepSeek, ThinkingLevel.Max, false,
            o => ExpectBody(o, """{"thinking":{"type":"enabled"},"reasoning_effort":"max"}""")),
        // DeepSeek maps medium to high on the server. Netclaw rejects it and never sends another level.
        ["deepseek_medium_throws_unsupported"] = new(ThinkingDialect.DeepSeek, ThinkingLevel.Medium, false,
            ExpectUnchanged, UnsupportedLevels: "off, low, high, max"),

        ["reasoning_effort_off_sets_none"] = new(ThinkingDialect.ReasoningEffort, ThinkingLevel.Off, false,
            o => ExpectBody(o, """{"reasoning_effort":"none"}""")),
        ["reasoning_effort_medium_sets_top_level_field"] = new(ThinkingDialect.ReasoningEffort, ThinkingLevel.Medium, false,
            o => ExpectBody(o, """{"reasoning_effort":"medium"}""")),
        ["reasoning_effort_max_throws_unsupported"] = new(ThinkingDialect.ReasoningEffort, ThinkingLevel.Max, false,
            ExpectUnchanged, UnsupportedLevels: "off, low, medium, high"),

        ["chat_template_kwargs_off_sets_enable_thinking_false"] = new(ThinkingDialect.ChatTemplateKwargs, ThinkingLevel.Off, false,
            o => ExpectBody(o, """{"chat_template_kwargs":{"enable_thinking":false}}""")),
        ["chat_template_kwargs_low_throws_unsupported"] = new(ThinkingDialect.ChatTemplateKwargs, ThinkingLevel.Low, false,
            ExpectUnchanged, UnsupportedLevels: "off"),

        ["ollama_think_off_sets_think_false"] = new(ThinkingDialect.OllamaThink, ThinkingLevel.Off, false,
            o => ExpectBody(o, """{"think":false}""")),
        ["ollama_think_low_sets_reasoning_low"] = new(ThinkingDialect.OllamaThink, ThinkingLevel.Low, false,
            o => ExpectEffort(o, ReasoningEffort.Low)),
        // OllamaSharp maps ExtraHigh to "high", so Max would silently send high.
        ["ollama_think_max_throws_unsupported"] = new(ThinkingDialect.OllamaThink, ThinkingLevel.Max, false,
            ExpectUnchanged, UnsupportedLevels: "off, low, medium, high"),

        ["none_low_throws_unsupported"] = new(ThinkingDialect.None, ThinkingLevel.Low, false, ExpectUnchanged, UnsupportedLevels: ""),

        ["unset_level_writes_nothing_native"] = new(ThinkingDialect.Native, null, false, ExpectUnchanged),
        ["unset_level_writes_nothing_deepseek"] = new(ThinkingDialect.DeepSeek, null, false, ExpectUnchanged),
        ["unset_level_writes_nothing_reasoning_effort"] = new(ThinkingDialect.ReasoningEffort, null, false, ExpectUnchanged),
        ["unset_level_writes_nothing_chat_template_kwargs"] = new(ThinkingDialect.ChatTemplateKwargs, null, false, ExpectUnchanged),
        ["unset_level_writes_nothing_ollama_think"] = new(ThinkingDialect.OllamaThink, null, false, ExpectUnchanged),
        ["unset_level_writes_nothing_none"] = new(ThinkingDialect.None, null, false, ExpectUnchanged),
        ["unset_level_writes_nothing_unresolved"] = new(ThinkingDialect.Unresolved, null, false, ExpectUnchanged),

        // A Native Off fails on a model that cannot turn thinking off, and memory curation would break (D8).
        ["suppress_on_native_writes_nothing"] = new(ThinkingDialect.Native, null, true, ExpectUnchanged),
        ["suppress_on_reasoning_effort_sets_none"] = new(ThinkingDialect.ReasoningEffort, null, true,
            o => ExpectBody(o, """{"reasoning_effort":"none"}""")),
        ["suppress_on_deepseek_sets_disabled"] = new(ThinkingDialect.DeepSeek, null, true,
            o => ExpectBody(o, """{"thinking":{"type":"disabled"}}""")),
        // Unresolved keeps today's suppression form, so memory curation does not change.
        ["suppress_on_unresolved_sets_enable_thinking_false"] = new(ThinkingDialect.Unresolved, null, true,
            o => ExpectBody(o, """{"chat_template_kwargs":{"enable_thinking":false}}""")),
    };

    public static TheoryData<string> RowNames => new(Rows.Keys);

    [Theory]
    [MemberData(nameof(RowNames))]
    public void Applies_level_to_request(string row)
    {
        var spec = Rows[row];
        var options = new ChatOptions();

        if (spec.UnsupportedLevels is { } offered)
        {
            var error = Assert.Throws<NotSupportedException>(
                () => ThinkingDialectMap.Apply(spec.Dialect, options, spec.Level));
            Assert.Contains(spec.Level!.Value.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
            if (offered.Length > 0)
                Assert.Contains(offered, error.Message, StringComparison.Ordinal);
        }
        else if (spec.Suppress)
        {
            ThinkingDialectMap.ApplySuppression(spec.Dialect, options);
        }
        else
        {
            ThinkingDialectMap.Apply(spec.Dialect, options, spec.Level);
        }

        spec.Expect(options);
    }

    [Fact]
    public void chat_template_kwargs_merges_caller_kwargs_without_changing_them()
    {
        var callerKwargs = new Dictionary<string, object?> { ["preserve_thinking"] = true };
        var options = new ChatOptions { AdditionalProperties = new() { ["chat_template_kwargs"] = callerKwargs } };

        ThinkingDialectMap.Apply(ThinkingDialect.ChatTemplateKwargs, options, ThinkingLevel.Off);

        ExpectBody(options, """{"chat_template_kwargs":{"preserve_thinking":true,"enable_thinking":false}}""");
        Assert.Equal(["preserve_thinking"], callerKwargs.Keys);
    }

    private static void ExpectEffort(ChatOptions options, ReasoningEffort effort)
    {
        Assert.Equal(effort, options.Reasoning?.Effort);
        Assert.True(options.AdditionalProperties is null or { Count: 0 }, "a Reasoning level writes no body field");
    }

    private static void ExpectUnchanged(ChatOptions options)
    {
        Assert.Null(options.Reasoning);
        Assert.True(options.AdditionalProperties is null or { Count: 0 }, "no request field may be written");
    }

    private static void ExpectBody(ChatOptions options, string expectedJson)
    {
        Assert.Null(options.Reasoning);
        Assert.NotNull(options.AdditionalProperties);
        var actual = JsonSerializer.SerializeToElement(options.AdditionalProperties);
        using var expected = JsonDocument.Parse(expectedJson);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual),
            $"expected body fields {expectedJson}, got {actual.GetRawText()}");
    }
}

/// <summary>
/// Offered levels = (levels the dialect can send) ∩ (levels the model accepts). Each level that is
/// not offered carries the reason that the CLI, the TUI, and doctor show (design D5).
/// </summary>
public sealed class OfferedLevelsTests
{
    private static readonly ThinkingLevel[] AllLevels =
        [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.Max];

    private sealed record Row(
        ThinkingDialect Dialect,
        ThinkingSupport Capability,
        ThinkingLevel[] Offered,
        string? Reason = null,
        ThinkingLevel ReasonLevel = ThinkingLevel.Off);

    private static readonly Dictionary<string, Row> Rows = new()
    {
        // Netclaw never guesses a level for an unknown model; Off is safe when the dialect can send it.
        ["unknown_with_dialect_offers_off_only"] = new(ThinkingDialect.ReasoningEffort, ThinkingSupport.Unknown,
            [ThinkingLevel.Off], "thinking support unknown for this endpoint", ThinkingLevel.Low),
        ["unknown_without_dialect_offers_nothing"] = new(ThinkingDialect.Unresolved, ThinkingSupport.Unknown,
            [], "ThinkingDialect"),
        // A Native Off fails on a hosted model that cannot turn thinking off, so an unknown hosted
        // model gets no level at all (review of PR #2694).
        ["native_unknown_offers_nothing"] = new(ThinkingDialect.Native, ThinkingSupport.Unknown,
            [], "thinking support unknown for this endpoint"),
        ["none_offers_nothing_with_reason"] = new(ThinkingDialect.Native, ThinkingSupport.None,
            [], "does not think"),
        ["always_on_offers_nothing_with_reason"] = new(ThinkingDialect.Native, ThinkingSupport.AlwaysOn,
            [], "always thinks"),
        ["controllable_without_off_greys_off"] = new(ThinkingDialect.Native,
            ThinkingSupport.Controllable(ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High),
            [ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High], "always thinks"),
        // The catalog accepts Medium, but the DeepSeek dialect cannot send it.
        ["controllable_levels_intersect_dialect"] = new(ThinkingDialect.DeepSeek,
            ThinkingSupport.Controllable(AllLevels),
            [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max]),
    };

    public static TheoryData<string> RowNames => new(Rows.Keys);

    [Theory]
    [MemberData(nameof(RowNames))]
    public void Intersects_dialect_and_capability(string row)
    {
        var spec = Rows[row];

        var result = ThinkingDialectMap.OfferedLevels(spec.Dialect, spec.Capability);

        Assert.Equal(spec.Offered, result.Offered);
        var missing = AllLevels.Except(spec.Offered).ToArray();
        Assert.Equal(missing.Order(), result.Unavailable.Keys.Order());
        Assert.All(result.Unavailable.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
        if (spec.Reason is { } reason)
            Assert.Contains(reason, result.Unavailable[spec.ReasonLevel], StringComparison.Ordinal);
    }
}
