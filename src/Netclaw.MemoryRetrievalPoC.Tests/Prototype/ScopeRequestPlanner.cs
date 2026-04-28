// -----------------------------------------------------------------------
// <copyright file="ScopeRequestPlanner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.MemoryRetrievalPoC.Tests.Prototype;

internal sealed class ScopeRequestPlanner
{
    private static readonly Regex TokenRegex = new("[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "about", "are", "at", "be", "did", "do", "for", "from", "how", "i", "if", "in", "is", "it", "of", "on", "or", "the", "to", "we", "what", "when", "where", "with", "you"
    ];

    private readonly IReadOnlyList<RetrievedDocument> _documents;
    private readonly IReadOnlyDictionary<string, string[]> _aliasesByAnchor;

    public ScopeRequestPlanner(IReadOnlyList<RetrievedDocument> documents, IReadOnlyList<RetrievedEdge> edges)
    {
        _documents = documents;
        _aliasesByAnchor = edges
            .Where(x => x.RelationType == "alias" && x.ToAnchorId.StartsWith("alias:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.FromAnchorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.Select(e => e.ToAnchorId["alias:".Length..]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public RetrievalRequestPlan Plan(QueryContext context)
    {
        var hardScope = ResolveHardScope(context);
        var tokens = Tokenize(context.Prompt).ToArray();
        var bigrams = MakeBigrams(tokens).ToArray();
        var anchorHints = InferAnchorHints(context.Prompt, tokens).ToArray();
        var softScopes = InferSoftScopes(context, tokens, anchorHints).ToArray();
        var facets = InferFacets(tokens, bigrams, anchorHints).ToArray();
        var mode = InferMode(tokens, facets);

        return new RetrievalRequestPlan(
            HardScope: hardScope,
            SoftScopes: softScopes,
            RetrievalMode: mode,
            LexicalTerms: tokens,
            Facets: facets,
            AnchorHints: anchorHints,
            CandidateLimit: mode == "bundle" ? 60 : 30,
            AllowedMemoryClasses: ["durable_fact"],
            ExcludedSensitivity: ["secret"],
            ExcludeExpired: true);
    }

    private static string ResolveHardScope(QueryContext context)
    {
        if (context.Surface == "slack_channel" && !string.IsNullOrWhiteSpace(context.ChannelDomain))
            return context.ChannelDomain!;

        if (context.Surface == "slack_dm" && !string.IsNullOrWhiteSpace(context.UserDomain))
            return context.UserDomain!;

        return context.UserDomain ?? context.ChannelDomain ?? "scope:default";
    }

    private IEnumerable<string> InferAnchorHints(string prompt, IReadOnlyList<string> tokens)
    {
        var normalizedPrompt = prompt.ToLowerInvariant();
        foreach (var document in _documents)
        {
            if (normalizedPrompt.Contains(document.AnchorId.Replace("anchor:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                yield return document.AnchorId;

            if (_aliasesByAnchor.TryGetValue(document.AnchorId, out var aliases)
                && aliases.Any(alias => normalizedPrompt.Contains(alias, StringComparison.OrdinalIgnoreCase)))
                yield return document.AnchorId;

            if (tokens.Any(token => document.Title.Contains(token, StringComparison.OrdinalIgnoreCase)))
                yield return document.AnchorId;
        }
    }

    private static IEnumerable<string> InferSoftScopes(QueryContext context, IReadOnlyList<string> tokens, IReadOnlyList<string> anchorHints)
    {
        if (!string.IsNullOrWhiteSpace(context.ThreadTitle))
            yield return context.ThreadTitle!;

        foreach (var anchor in anchorHints.Take(3))
            yield return anchor;

        if (tokens.Contains("textforge"))
            yield return "project:textforge";

        if (tokens.Any(x => x is "travel" or "flight" or "airport" or "airline" or "hotel" or "trip"))
            yield return "scope:travel";

        if (tokens.Any(x => x is "queue" or "backlog" or "dashboard" or "incident"))
            yield return "scope:ops";

        if (tokens.Any(x => x is "homepage" or "copy" or "feature" or "pricing"))
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

    private static string InferMode(IReadOnlyList<string> tokens, IReadOnlyList<string> facets)
    {
        var wantsBundle = facets.Contains("trip_planning")
            || (facets.Contains("travel_profile") && tokens.Any(x => x is "what" or "which" or "book" or "best"));

        return wantsBundle ? "bundle" : "ranked";
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in TokenRegex.Matches(text.ToLowerInvariant()))
        {
            var token = match.Value;
            if (token.Length < 2 || StopWords.Contains(token))
                continue;
            yield return token;
        }
    }

    private static IEnumerable<string> MakeBigrams(IReadOnlyList<string> tokens)
    {
        for (var i = 1; i < tokens.Count; i++)
            yield return tokens[i - 1] + " " + tokens[i];
    }
}

internal sealed record QueryContext(
    string Surface,
    string Prompt,
    string? UserDomain,
    string? ChannelDomain,
    string? ThreadTitle = null);

internal sealed record RetrievalRequestPlan(
    string HardScope,
    IReadOnlyList<string> SoftScopes,
    string RetrievalMode,
    IReadOnlyList<string> LexicalTerms,
    IReadOnlyList<string> Facets,
    IReadOnlyList<string> AnchorHints,
    int CandidateLimit,
    IReadOnlyList<string> AllowedMemoryClasses,
    IReadOnlyList<string> ExcludedSensitivity,
    bool ExcludeExpired);
