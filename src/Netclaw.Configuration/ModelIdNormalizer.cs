using System.Text.RegularExpressions;

namespace Netclaw.Configuration;

/// <summary>
/// Normalizes model IDs across provider naming conventions to enable
/// cross-provider capability lookups. Produces candidate IDs for matching.
/// </summary>
public static partial class ModelIdNormalizer
{
    // Matches date suffixes like -20250514, -20260101
    [GeneratedRegex(@"-\d{8}$")]
    private static partial Regex DateSuffixPattern();

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
    /// The first entry is always the original ID.
    /// </summary>
    public static IReadOnlyList<string> GetCandidates(string modelId)
    {
        var candidates = new List<string> { modelId };
        var working = modelId;

        // Strip Ollama tag suffixes (:latest, :7b, :q4_0, etc.)
        var colonIndex = working.IndexOf(':');
        if (colonIndex > 0)
        {
            var stripped = working[..colonIndex];
            if (!candidates.Contains(stripped))
                candidates.Add(stripped);
            working = stripped;
        }

        // Strip date suffixes (-20250514)
        var withoutDate = DateSuffixPattern().Replace(working, "");
        if (withoutDate != working && !candidates.Contains(withoutDate))
        {
            candidates.Add(withoutDate);
        }

        // If no slash (not already prefixed), try adding known provider prefixes
        if (!working.Contains('/'))
        {
            foreach (var (prefix, provider) in KnownPrefixes)
            {
                if (working.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var prefixed = $"{provider}/{working}";
                    if (!candidates.Contains(prefixed))
                        candidates.Add(prefixed);

                    // Also try prefixed + date-stripped
                    if (withoutDate != working)
                    {
                        var prefixedNoDate = $"{provider}/{withoutDate}";
                        if (!candidates.Contains(prefixedNoDate))
                            candidates.Add(prefixedNoDate);
                    }

                    break;
                }
            }
        }
        else
        {
            // Already has a prefix — try date-stripped version with prefix
            var prefixedWithoutDate = DateSuffixPattern().Replace(modelId, "");
            if (prefixedWithoutDate != modelId && !candidates.Contains(prefixedWithoutDate))
                candidates.Add(prefixedWithoutDate);
        }

        return candidates;
    }
}
