// -----------------------------------------------------------------------
// <copyright file="TextTokenizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Actors.Text;

/// <summary>
/// Shared text tokenization utilities for deterministic NLP.
/// Used by memory recall planning, candidate selection, and skill trigger matching.
/// </summary>
public static class TextTokenizer
{
    private static readonly Regex TokenRegex = new("[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "about", "are", "at", "be", "did", "do", "for",
        "from", "how", "i", "if", "in", "is", "it", "of", "on", "or",
        "the", "to", "we", "what", "when", "where", "with", "you"
    ];

    /// <summary>
    /// Tokenize text into lowercase alphanumeric tokens with stopword removal
    /// and plural normalization. Tokens shorter than 2 characters are excluded.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var results = new List<string>();
        foreach (Match match in TokenRegex.Matches(text.ToLowerInvariant()))
        {
            var token = match.Value;
            if (token.Length < 2 || StopWords.Contains(token))
                continue;
            results.Add(NormalizePlural(token));
        }

        return results;
    }

    /// <summary>
    /// Generate bigrams (consecutive token pairs) for phrase matching.
    /// </summary>
    public static IReadOnlyList<string> MakeBigrams(IReadOnlyList<string> tokens)
    {
        var results = new List<string>(Math.Max(0, tokens.Count - 1));
        for (var i = 1; i < tokens.Count; i++)
            results.Add(tokens[i - 1] + " " + tokens[i]);
        return results;
    }

    /// <summary>
    /// Normalize common English plural suffixes to singular form.
    /// </summary>
    public static string NormalizePlural(string token)
    {
        // "categories" → "category"
        if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal))
            return token[..^3] + "y";

        // "matches" → "match", "buses" → "bus" (sibilant + es)
        if (token.Length > 4 && token.EndsWith("es", StringComparison.Ordinal))
        {
            var beforeEs = token[^3];
            if (beforeEs is 's' or 'x' or 'z')
                return token[..^2];
            if (token.Length > 5 && token[^4..^2] is "ch" or "sh")
                return token[..^2];
        }

        // "prices" → "price", "flights" → "flight" (general trailing s)
        if (token.Length > 3 && token[^1] == 's' && token[^2] != 's')
            return token[..^1];

        return token;
    }
}
