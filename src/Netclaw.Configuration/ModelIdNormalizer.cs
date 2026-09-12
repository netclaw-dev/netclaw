// -----------------------------------------------------------------------
// <copyright file="ModelIdNormalizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Configuration;

/// <summary>
/// Normalizes model IDs across provider naming conventions to enable
/// cross-provider capability lookups. Produces candidate IDs for matching
/// and human-friendly display names.
/// </summary>
public static partial class ModelIdNormalizer
{
    // Matches date suffixes like -20250514, -20260101
    [GeneratedRegex(@"-\d{8}$")]
    private static partial Regex DateSuffixPattern();

    // Matches .gguf file extension (case-insensitive)
    [GeneratedRegex(@"\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex GgufExtensionPattern();

    // Matches GGML quantization suffixes: -Q4_0, -Q5_K_M, -IQ2_XXS, -Q4_K_XL, etc.
    [GeneratedRegex(@"[-_]I?Q\d(?:_[A-Z0-9]{1,4})*(?:[-_]XL)?$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantizationSuffixPattern();

    // Known provider prefixes for bare model IDs
    private static readonly Dictionary<string, string> KnownPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "anthropic",
        ["gpt"] = "openai",
        ["o1"] = "openai",
        ["o3"] = "openai",
        ["o4"] = "openai",
        ["gemini"] = "google",
        ["gemma"] = "google",
        ["llama"] = "meta-llama",
        ["mistral"] = "mistralai",
        ["mixtral"] = "mistralai",
        ["qwen"] = "qwen",
        ["deepseek"] = "deepseek",
        ["phi"] = "microsoft",
    };

    /// <summary>
    /// Produces a set of candidate model IDs for cross-provider lookup.
    /// The first entry is always the original ID. Candidates are ordered
    /// from most specific (raw) to most normalized.
    /// </summary>
    public static IReadOnlyList<string> GetCandidates(string modelId)
    {
        var candidates = new List<string> { modelId };
        var working = modelId;

        // Step 1: Strip Ollama tag suffixes (:latest, :7b, :q4_0, etc.)
        var colonIndex = working.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex > 0)
        {
            working = working[..colonIndex];
            AddIfNew(candidates, working);
        }

        // Step 2: Strip .gguf file extension
        var afterGguf = GgufExtensionPattern().Replace(working, "");
        if (afterGguf != working)
        {
            AddIfNew(candidates, afterGguf);
            working = afterGguf;
        }

        // Step 3: Strip quantization suffix (-Q5_K_M, -q4_0, -IQ2_XXS, etc.)
        var afterQuant = QuantizationSuffixPattern().Replace(working, "");
        if (afterQuant != working)
        {
            AddIfNew(candidates, afterQuant);
            working = afterQuant;
        }

        // Step 4: Lowercase normalization (GGUF PascalCase → catalog lowercase)
        var lowered = working.ToLowerInvariant();
        if (lowered != working)
        {
            AddIfNew(candidates, lowered);
            working = lowered;
        }

        // Step 5: Trailing-segment stripping (catches build variant tags like -UD, -BPW4)
        var lastDash = working.LastIndexOf('-');
        if (lastDash > 0)
        {
            var withoutTrailing = working[..lastDash];
            AddIfNew(candidates, withoutTrailing);
        }

        // Step 6: Strip date suffixes (-20250514)
        var withoutDate = DateSuffixPattern().Replace(working, "");
        if (withoutDate != working)
            AddIfNew(candidates, withoutDate);

        // Step 7: Add known provider prefixes for unprefixed forms
        if (!working.Contains('/', StringComparison.Ordinal))
        {
            foreach (var (prefix, provider) in KnownPrefixes)
            {
                if (working.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Prefix all unique unprefixed candidates
                    var unprefixed = candidates
                        .Where(c => !c.Contains('/', StringComparison.Ordinal))
                        .ToList();
                    foreach (var form in unprefixed)
                    {
                        AddIfNew(candidates, $"{provider}/{form}");
                    }

                    break;
                }
            }
        }
        else
        {
            // Already has a prefix — try date-stripped version with prefix
            var prefixedWithoutDate = DateSuffixPattern().Replace(modelId, "");
            if (prefixedWithoutDate != modelId)
                AddIfNew(candidates, prefixedWithoutDate);
        }

        return candidates;
    }

    /// <summary>
    /// Produces a human-friendly display name by stripping file-format noise
    /// (.gguf extension, quantization suffixes, Ollama tags) while preserving
    /// the original casing and meaningful name segments.
    /// </summary>
    public static string GetDisplayName(string modelId)
    {
        var name = modelId;

        // Strip Ollama tag
        var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx > 0)
            name = name[..colonIdx];

        // Strip .gguf extension
        name = GgufExtensionPattern().Replace(name, "");

        // Strip quantization suffix
        name = QuantizationSuffixPattern().Replace(name, "");

        return name;
    }

    private static void AddIfNew(List<string> candidates, string value)
    {
        if (!candidates.Contains(value))
            candidates.Add(value);
    }
}
