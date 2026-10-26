// -----------------------------------------------------------------------
// <copyright file="ThinkingDialectMap.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Providers;

/// <summary>
/// The levels that a model offers, and the reason for each level that it does not offer.
/// <see cref="Unavailable"/> holds every <see cref="ThinkingLevel"/> that is not in <see cref="Offered"/>.
/// </summary>
public sealed record OfferedThinkingLevels(
    IReadOnlyList<ThinkingLevel> Offered,
    IReadOnlyDictionary<ThinkingLevel, string> Unavailable);

/// <summary>
/// The static map of each <see cref="ThinkingDialect"/>: the levels that it can send, and the request
/// fields that it writes for a level and for the reasoning suppression intent.
/// </summary>
public static class ThinkingDialectMap
{
    public const string VendorOptionKey = "ThinkingDialect";

    /// <summary>The dialects that an operator can set in <c>Providers:&lt;name&gt;:VendorOptions:ThinkingDialect</c>.</summary>
    public static IReadOnlyList<ThinkingDialect> ConfigurableDialects { get; } =
        [ThinkingDialect.ChatTemplateKwargs, ThinkingDialect.ReasoningEffort, ThinkingDialect.DeepSeek, ThinkingDialect.None];

    /// <summary>The configurable values as text, for example "ChatTemplateKwargs, ReasoningEffort, DeepSeek, or None".</summary>
    public static string ConfigurableDialectList { get; } =
        string.Join(", ", ConfigurableDialects.Take(ConfigurableDialects.Count - 1)) + ", or " + ConfigurableDialects[^1];

    private static readonly ThinkingLevel[] AllLevels =
        [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.Max];

    private const string UnknownCapabilityReason = "thinking support unknown for this endpoint";

    private static readonly string UnresolvedReason =
        $"no thinking dialect is set; set VendorOptions:{VendorOptionKey} on the provider to {ConfigurableDialectList}";

    /// <summary>The levels that the dialect can send, in enum order.</summary>
    public static IReadOnlyList<ThinkingLevel> ExpressibleLevels(ThinkingDialect dialect) => dialect switch
    {
        ThinkingDialect.Native => AllLevels,
        // The DeepSeek API lists none, low, high, and max; it has no medium.
        ThinkingDialect.DeepSeek => [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max],
        ThinkingDialect.ReasoningEffort => [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High],
        ThinkingDialect.ChatTemplateKwargs => [ThinkingLevel.Off],
        // OllamaSharp maps ExtraHigh to "high", so Max would silently send high.
        ThinkingDialect.OllamaThink => [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High],
        ThinkingDialect.None or ThinkingDialect.Unresolved => [],
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown thinking dialect."),
    };

    /// <summary>
    /// The offered levels: the levels that the dialect can send and that the capability includes.
    /// Netclaw never guesses a level for a model with an unknown capability.
    /// </summary>
    public static OfferedThinkingLevels OfferedLevels(ThinkingDialect dialect, ThinkingSupport capability)
    {
        var expressible = ExpressibleLevels(dialect);
        var offered = new List<ThinkingLevel>();
        var unavailable = new Dictionary<ThinkingLevel, string>();
        foreach (var level in AllLevels)
        {
            var reason = dialect == ThinkingDialect.Unresolved
                ? UnresolvedReason
                : !expressible.Contains(level)
                    ? $"the {dialect} dialect cannot send {Format(level)}"
                    : CapabilityReason(dialect, capability, level);
            if (reason is null)
                offered.Add(level);
            else
                unavailable[level] = reason;
        }

        return new OfferedThinkingLevels(offered, unavailable);
    }

    // Off is safe for an unknown model only when a body field sends it: a self-hosted server ignores
    // a field that its template does not read. A Native Off fails on a hosted model that cannot turn
    // thinking off, so an unknown Native model gets no level.
    private static string? CapabilityReason(ThinkingDialect dialect, ThinkingSupport capability, ThinkingLevel level) => capability.Kind switch
    {
        ThinkingSupportKind.Unknown =>
            level == ThinkingLevel.Off && dialect != ThinkingDialect.Native ? null : UnknownCapabilityReason,
        ThinkingSupportKind.None => "this model does not think",
        ThinkingSupportKind.AlwaysOn => "this model always thinks; the endpoint reports no levels",
        ThinkingSupportKind.Controllable when capability.Levels.Contains(level) => null,
        ThinkingSupportKind.Controllable when level == ThinkingLevel.Off =>
            "this model always thinks; it cannot turn thinking off",
        ThinkingSupportKind.Controllable => $"this model does not accept {Format(level)}",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability.Kind, "Unknown thinking support."),
    };

    /// <summary>
    /// Writes the request fields for <paramref name="level"/> into <paramref name="options"/>. A null level
    /// writes nothing. Throws <see cref="NotSupportedException"/> when the dialect cannot send the level.
    /// </summary>
    public static void Apply(ThinkingDialect dialect, ChatOptions options, ThinkingLevel? level)
    {
        if (!level.HasValue)
            return;

        var value = level.Value;

        var expressible = ExpressibleLevels(dialect);
        if (!expressible.Contains(value))
        {
            // Never map to a nearby level: the operator would pay for a level that they did not ask for.
            throw new NotSupportedException(expressible.Count == 0
                ? $"The {dialect} thinking dialect cannot send thinking level '{Format(value)}'. It sends no thinking level."
                : $"The {dialect} thinking dialect cannot send thinking level '{Format(value)}'. It sends: {Format(expressible)}.");
        }

        switch (dialect)
        {
            case ThinkingDialect.Native:
                options.Reasoning = new ReasoningOptions { Effort = ToEffort(value) };
                break;
            case ThinkingDialect.OllamaThink when value == ThinkingLevel.Off:
                Body(options)["think"] = false;
                break;
            case ThinkingDialect.OllamaThink:
                // OllamaSharp maps these efforts to the "low", "medium", and "high" think strings.
                options.Reasoning = new ReasoningOptions { Effort = ToEffort(value) };
                break;
            case ThinkingDialect.DeepSeek when value == ThinkingLevel.Off:
                Body(options)["thinking"] = new Dictionary<string, object?> { ["type"] = "disabled" };
                break;
            case ThinkingDialect.DeepSeek:
                Body(options)["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled" };
                Body(options)["reasoning_effort"] = Format(value);
                break;
            case ThinkingDialect.ReasoningEffort:
                Body(options)["reasoning_effort"] = value == ThinkingLevel.Off ? "none" : Format(value);
                break;
            case ThinkingDialect.ChatTemplateKwargs:
                MergeChatTemplateKwargs(options, enableThinking: false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown thinking dialect.");
        }
    }

    /// <summary>
    /// Writes the request fields for the reasoning suppression intent of a call site into
    /// <paramref name="options"/>. It never throws.
    /// </summary>
    public static void ApplySuppression(ThinkingDialect dialect, ChatOptions options)
    {
        switch (dialect)
        {
            // A Native Off fails on a model that cannot turn thinking off (D8), so a strict SDK
            // gets no thinking field, as before this change.
            case ThinkingDialect.Native:
            case ThinkingDialect.None:
                break;
            // Unresolved keeps the suppression form that it had before this change.
            case ThinkingDialect.Unresolved:
                MergeChatTemplateKwargs(options, enableThinking: false);
                break;
            default:
                Apply(dialect, options, ThinkingLevel.Off);
                break;
        }
    }

    /// <summary>True when the suppression intent writes an explicit Off into the request.</summary>
    public static bool SuppressionSendsOff(ThinkingDialect dialect)
        => dialect is not (ThinkingDialect.Native or ThinkingDialect.None);

    /// <summary>The level of a caller's <see cref="ReasoningEffort"/>.</summary>
    public static ThinkingLevel LevelOf(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => ThinkingLevel.Off,
        ReasoningEffort.Low => ThinkingLevel.Low,
        ReasoningEffort.Medium => ThinkingLevel.Medium,
        ReasoningEffort.High => ThinkingLevel.High,
        ReasoningEffort.ExtraHigh => ThinkingLevel.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown reasoning effort."),
    };

    /// <summary>The level in the lower-case form that the CLI and the TUI use.</summary>
    public static string Format(ThinkingLevel level) => level.ToString().ToLowerInvariant();

    /// <summary>The levels as a list, for example "off, low, high, max".</summary>
    public static string Format(IEnumerable<ThinkingLevel> levels) => string.Join(", ", levels.Select(Format));

    /// <summary>
    /// Parses a configured dialect. Accepts the enum name and the kebab-case form, for example
    /// <c>chat-template-kwargs</c>. Returns false for any other value, including a number.
    /// </summary>
    public static bool TryParseConfigured(string? text, out ThinkingDialect dialect)
    {
        dialect = default;
        var normalized = text?.Replace("-", "", StringComparison.Ordinal).Trim();
        foreach (var candidate in ConfigurableDialects)
        {
            if (string.Equals(candidate.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                dialect = candidate;
                return true;
            }
        }

        return false;
    }

    private static ReasoningEffort ToEffort(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => ReasoningEffort.None,
        ThinkingLevel.Low => ReasoningEffort.Low,
        ThinkingLevel.Medium => ReasoningEffort.Medium,
        ThinkingLevel.High => ReasoningEffort.High,
        // A plugin whose wire has a level above ExtraHigh (Anthropic, OpenRouter, Venice "max")
        // maps ExtraHigh to that level, because Max means the highest level that the provider offers.
        ThinkingLevel.Max => ReasoningEffort.ExtraHigh,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown thinking level."),
    };

    private static AdditionalPropertiesDictionary Body(ChatOptions options)
        => options.AdditionalProperties ??= new AdditionalPropertiesDictionary();

    // The caller's chat_template_kwargs object is never changed: the dialect writes a new dictionary
    // with the caller's entries and the thinking switch.
    private static void MergeChatTemplateKwargs(ChatOptions options, bool enableThinking)
    {
        var body = Body(options);
        var merged = new Dictionary<string, object?>();
        if (body.TryGetValue("chat_template_kwargs", out var existing) && existing is not null)
        {
            switch (existing)
            {
                case IDictionary<string, object?> dictionary:
                    foreach (var (key, value) in dictionary)
                        merged[key] = value;
                    break;
                case JsonObject json:
                    foreach (var (key, value) in json)
                        merged[key] = value?.DeepClone();
                    break;
                default:
                    throw new InvalidOperationException(
                        $"chat_template_kwargs has an unsupported type '{existing.GetType().Name}'. Use a dictionary or a JSON object.");
            }
        }

        merged["enable_thinking"] = enableThinking;
        body["chat_template_kwargs"] = merged;
    }
}
