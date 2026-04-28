// -----------------------------------------------------------------------
// <copyright file="DeterministicRetrievalPlanning.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Text;
using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions;

public enum DeterministicRetrievalMode
{
    Ranked,
    Bundle
}

public sealed record DeterministicRetrievalRequestPlan(
    IReadOnlyList<string> SoftScopes,
    DeterministicRetrievalMode RetrievalMode,
    IReadOnlyList<string> LexicalTerms,
    IReadOnlyList<string> Facets,
    IReadOnlyList<string> AnchorHints,
    int CandidateLimit,
    IReadOnlyList<string> AllowedMemoryClasses,
    IReadOnlyList<string> ExcludedSensitivity,
    bool ExcludeExpired);

public sealed class DeterministicRetrievalRequestPlanner
{
    public DeterministicRetrievalRequestPlan Plan(AutomaticRecallRequest request)
    {
        var prompt = string.IsNullOrWhiteSpace(request.Query)
            ? request.RecentUserMessages.LastOrDefault() ?? string.Empty
            : request.Query;
        var tokens = TextTokenizer.Tokenize(prompt);
        var bigrams = TextTokenizer.MakeBigrams(tokens);
        var anchorHints = InferAnchorHints(request, prompt, tokens).ToArray();
        var softScopes = InferSoftScopes(request, tokens, anchorHints).ToArray();
        var facets = InferFacets(tokens, bigrams, anchorHints).ToArray();
        var retrievalMode = InferMode(tokens, facets);

        return new DeterministicRetrievalRequestPlan(
            SoftScopes: softScopes,
            RetrievalMode: retrievalMode,
            LexicalTerms: CapLexicalTerms(tokens, anchorHints),
            Facets: facets,
            AnchorHints: anchorHints,
            CandidateLimit: retrievalMode == DeterministicRetrievalMode.Bundle ? BundleCandidateLimit : RankedCandidateLimit,
            AllowedMemoryClasses: [MemoryClass.DurableFact.ToWireValue(), MemoryClass.Evidence.ToWireValue()],
            ExcludedSensitivity: [MemorySensitivity.Secret.ToWireValue()],
            ExcludeExpired: true);
    }

    // Words that commonly appear in conversational messages but carry no
    // semantic weight for recall. Used to filter both anchor hints (issue #582)
    // AND lexical terms (issue #693). Production evidence showed that generic
    // words like "can", "that", "ok" cause false positives even as lexical
    // terms — a query containing "ok can that this" would match any memory
    // containing the word "that", polluting the recall with unrelated content.
    private static readonly HashSet<string> RecallStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Question/modal words
        "which", "what", "when", "where", "how", "why", "who", "whose",
        "can", "could", "should", "would", "may", "might", "will", "shall",
        // Determiners and demonstratives
        "the", "this", "that", "these", "those", "there", "here",
        // Be/have/do auxiliaries
        "does", "did", "has", "have", "had", "was", "were", "are", "is", "be", "been", "being",
        // Pronouns that commonly start sentences
        "my", "our", "your", "their", "his", "her", "its",
        "i", "you", "we", "they", "he", "she", "it",
        // Imperative lead-ins
        "tell", "show", "give", "let", "please", "kindly",
        // Conjunctions / sentence glue
        "but", "so", "yet", "and", "or", "nor", "for",
        // Conversational fillers
        "ok", "okay", "yeah", "yes", "no", "sure", "right", "well",
    };

    private static IEnumerable<string> InferAnchorHints(AutomaticRecallRequest request, string prompt, IReadOnlyList<string> tokens)
    {
        foreach (var entity in request.RecentEntities ?? [])
            if (!string.IsNullOrWhiteSpace(entity))
                yield return entity.Trim();

        foreach (Match match in Regex.Matches(prompt, "\\b[A-Z][A-Za-z0-9._-]{2,}\\b"))
        {
            // Filter sentence-start capitalized stopwords. These pull
            // unrelated ops/eval docs into recall (issue #582).
            if (RecallStopWords.Contains(match.Value))
                continue;
            yield return match.Value;
        }

        if (tokens.Contains("textforge"))
            yield return "textforge";
        if (tokens.Contains("stir") || tokens.Contains("trek"))
            yield return "stirtrek";
        if (tokens.Contains("queue") || tokens.Contains("backlog"))
            yield return "worker-b";
    }

    private static IEnumerable<string> InferSoftScopes(AutomaticRecallRequest request, IReadOnlyList<string> tokens, IReadOnlyList<string> anchorHints)
    {
        if (!string.IsNullOrWhiteSpace(request.ThreadTitle))
            yield return request.ThreadTitle!;

        foreach (var hint in anchorHints.Take(3))
            yield return hint;

        if (tokens.Any(x => x is "travel" or "flight" or "airport" or "airline" or "hotel" or "trip"))
            yield return "scope:travel";

        if (tokens.Any(x => x is "queue" or "backlog" or "dashboard" or "incident"))
            yield return "scope:ops";

        if (tokens.Any(x => x is "homepage" or "copy" or "feature" or "pricing") || anchorHints.Any(x => x.Contains("textforge", StringComparison.OrdinalIgnoreCase)))
            yield return "scope:product-marketing";
    }

    private static IEnumerable<string> InferFacets(IReadOnlyList<string> tokens, IReadOnlyList<string> bigrams, IReadOnlyList<string> anchorHints)
    {
        if (tokens.Any(x => x is "flight" or "fly" or "airport" or "airline" or "trip" or "travel"))
            yield return "travel_profile";

        if (tokens.Any(x => x is "hotel" or "rental" or "venue") || bigrams.Contains("stir trek"))
            yield return "trip_planning";

        if (tokens.Any(x => x is "queue" or "backlog" or "incident" || x == "dashboard"))
            yield return "incident_recovery";

        if (tokens.Any(x => x is "pricing" || x == "homepage") || anchorHints.Any(x => x.Contains("textforge", StringComparison.OrdinalIgnoreCase)))
            yield return "project_fact";
    }

    private const int RankedCandidateLimit = 15;
    private const int BundleCandidateLimit = 20;
    private const int MaxLexicalTerms = 12;

    private static IReadOnlyList<string> CapLexicalTerms(IReadOnlyList<string> tokens, IReadOnlyList<string> anchorHints)
    {
        // Filter conversational stopwords that cause false positives (issue #693).
        // Generic words like "ok", "can", "that" match any memory containing them.
        var filtered = tokens.Where(t => !RecallStopWords.Contains(t)).ToList();

        if (filtered.Count == 0)
            return filtered;

        if (filtered.Count <= MaxLexicalTerms)
            return filtered;

        var anchorSet = new HashSet<string>(anchorHints, StringComparer.OrdinalIgnoreCase);

        // Promote tokens that appear in anchor hints, then sort remaining by
        // length descending (longer tokens are more discriminative).
        return filtered
            .OrderByDescending(t => anchorSet.Contains(t) ? 1 : 0)
            .ThenByDescending(t => t.Length)
            .Take(MaxLexicalTerms)
            .ToArray();
    }

    private static DeterministicRetrievalMode InferMode(IReadOnlyList<string> tokens, IReadOnlyList<string> facets)
    {
        var wantsBundle = facets.Contains("trip_planning")
            || (facets.Contains("travel_profile") && tokens.Any(x => x is "what" or "which" or "book" or "best"));

        return wantsBundle ? DeterministicRetrievalMode.Bundle : DeterministicRetrievalMode.Ranked;
    }

}
