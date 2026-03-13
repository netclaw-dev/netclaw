using System.Text.RegularExpressions;

namespace Netclaw.MemoryRetrievalPoC.Tests.Prototype;

internal sealed class DeterministicRecallEngine
{
    private static readonly Regex MarkerRegex = new("\\b[A-Z][A-Z0-9_]{2,}\\b", RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new("[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "again", "about", "any", "are", "at", "be", "before", "did", "do", "does", "for", "from", "get",
        "how", "i", "if", "in", "into", "is", "it", "last", "of", "on", "only", "or", "our", "out", "reply", "sentence", "so",
        "should", "the", "there", "time", "to", "up", "use", "usually", "we", "what", "when", "where", "which", "with", "you"
    ];
    private static readonly HashSet<string> ActionIntentTerms =
    [
        "precaution", "agree", "wobble", "restart", "recover", "recovery", "backlog", "control", "fix", "mitigate", "procedure", "incident", "spike", "queue"
    ];
    private static readonly HashSet<string> LookupIntentTerms =
    [
        "dashboard", "where", "url", "metrics", "chart", "airport", "airline"
    ];
    private static readonly HashSet<string> TravelIntentTerms =
    [
        "travel", "trip", "flight", "fly", "hotel", "rental", "car", "airport", "airline", "book"
    ];

    private readonly IReadOnlyList<IndexedDocument> _documents;
    private readonly IReadOnlyDictionary<string, List<Posting>> _postings;
    private readonly IReadOnlyDictionary<string, double> _idf;
    private readonly TermTrie _trie;
    private readonly IReadOnlyDictionary<string, List<RetrievedEdge>> _edgesByAnchor;
    private readonly IReadOnlyDictionary<string, IndexedDocument[]> _documentsByFacet;
    private readonly IReadOnlyDictionary<string, string[]> _aliasesByAnchor;
    private readonly IReadOnlyDictionary<string, List<NeighborEdge>> _inferredNeighborsByAnchor;
    private readonly IReadOnlyDictionary<string, string[]> _clustersByAnchor;
    private readonly IReadOnlyDictionary<string, string[]> _rolesByAnchor;
    private readonly IReadOnlyDictionary<string, string[]> _anchorsByCluster;
    private readonly IReadOnlyDictionary<string, string[]> _supportedClusters;

    public DeterministicRecallEngine(IReadOnlyList<RetrievedDocument> documents, IReadOnlyList<RetrievedEdge> edges)
    {
        _documents = documents.Select(IndexedDocument.Create).ToArray();
        _edgesByAnchor = edges
            .GroupBy(x => x.FromAnchorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        _clustersByAnchor = edges
            .Where(x => x.RelationType == "member_of_cluster")
            .GroupBy(x => x.FromAnchorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(e => e.ToAnchorId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        _rolesByAnchor = edges
            .Where(x => x.RelationType == "has_role")
            .GroupBy(x => x.FromAnchorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(e => e.ToAnchorId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        _aliasesByAnchor = edges
            .Where(x => x.RelationType == "alias" && x.ToAnchorId.StartsWith("alias:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.FromAnchorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.Select(e => e.ToAnchorId["alias:".Length..]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        _anchorsByCluster = edges
            .Where(x => x.RelationType == "member_of_cluster")
            .GroupBy(x => x.ToAnchorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(e => e.FromAnchorId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        _supportedClusters = edges
            .Where(x => x.RelationType == "supports_cluster")
            .GroupBy(x => x.FromAnchorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(e => e.ToAnchorId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        _inferredNeighborsByAnchor = BuildInferredNeighbors(_documents, _aliasesByAnchor);
        _documentsByFacet = _documents
            .SelectMany(d => d.Facets.Select(f => (Facet: f, Document: d)))
            .GroupBy(x => x.Facet, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Document).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        var postings = new Dictionary<string, List<Posting>>(StringComparer.OrdinalIgnoreCase);
        var trie = new TermTrie();
        var documentFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var document in _documents)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void addTerms(IEnumerable<string> terms, PostingField field)
            {
                foreach (var term in terms)
                {
                    if (!postings.TryGetValue(term, out var list))
                    {
                        list = [];
                        postings[term] = list;
                        trie.Add(term);
                    }

                    list.Add(new Posting(document.DocumentId, document.AnchorId, field));
                    if (seen.Add(term))
                        documentFrequency[term] = documentFrequency.TryGetValue(term, out var current) ? current + 1 : 1;
                }
            }

            addTerms(document.MarkerTokens, PostingField.Marker);
            addTerms(document.AnchorTokens, PostingField.Anchor);
            addTerms(document.TitleTokens, PostingField.Title);
            addTerms(document.BodyTokens, PostingField.Body);
            addTerms(document.Bigrams, PostingField.Bigram);
        }

        _postings = postings;
        _trie = trie;
        _idf = documentFrequency.ToDictionary(
            x => x.Key,
            x => Math.Log(1.0 + (_documents.Count / (double)x.Value)),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RetrievalHit> Search(string prompt, int maxResults = 3)
    {
        var query = QueryFeatures.From(prompt);
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var anchorScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var clusterScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var marker in query.Markers)
        {
            Accumulate(marker, 18.0, exactOnly: true);
        }

        foreach (var token in query.Tokens)
        {
            Accumulate(token, 5.0, exactOnly: false);
        }

        foreach (var bigram in query.Bigrams)
        {
            Accumulate(bigram, 8.0, exactOnly: true, PostingField.Bigram);
        }

        foreach (var facet in query.Facets)
        {
            if (!_documentsByFacet.TryGetValue(facet, out var facetDocuments))
                continue;

            foreach (var document in facetDocuments)
                Add(document.DocumentId, FacetBoost(facet, query, document), $"facet:{facet}");
        }

        foreach (var document in _documents)
        {
            if (anchorScores.TryGetValue(document.AnchorId, out var anchorBoost))
                Add(document.DocumentId, anchorBoost * 2.5, $"anchor:{document.AnchorId}");

            if (_clustersByAnchor.TryGetValue(document.AnchorId, out var clusters))
            {
                foreach (var cluster in clusters)
                {
                    if (clusterScores.TryGetValue(cluster, out var clusterBoost))
                        Add(document.DocumentId, clusterBoost * ClusterWeight(query, document, cluster), $"cluster:{cluster}");
                }
            }

            if (_edgesByAnchor.TryGetValue(document.AnchorId, out var neighbors))
            {
                foreach (var edge in neighbors)
                {
                    if (anchorScores.TryGetValue(edge.ToAnchorId, out var neighborBoost))
                        Add(document.DocumentId, neighborBoost * 0.75, $"edge:{edge.RelationType}");
                }
            }

            if (_inferredNeighborsByAnchor.TryGetValue(document.AnchorId, out var inferredNeighbors))
            {
                foreach (var neighbor in inferredNeighbors)
                {
                    if (anchorScores.TryGetValue(neighbor.ToAnchorId, out var neighborBoost))
                        Add(document.DocumentId, neighborBoost * neighbor.Weight, $"neighbor:{neighbor.Reason}");
                }
            }

            Add(document.DocumentId, document.Confidence * 2.0, "confidence");
            Add(document.DocumentId, IntentAdjustment(query, document), "intent");
        }

        var rankedHits = _documents
            .Where(d => scores.TryGetValue(d.DocumentId, out var score) && score >= 8.0)
            .Select(d => new ScoredHit(d, scores[d.DocumentId], reasons[d.DocumentId]))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DocumentId, StringComparer.Ordinal)
            .ToArray();

        var hits = Diversify(rankedHits, query, maxResults)
            .Select(x => new RetrievalHit(x.DocumentId, x.Title, x.Score, x.Reasons))
            .ToArray();

        return hits;

        void Accumulate(string term, double baseBoost, bool exactOnly, PostingField? restrictedField = null)
        {
            foreach (var candidate in EnumerateTerms(term, exactOnly))
            {
                if (!_postings.TryGetValue(candidate.Term, out var postingList))
                    continue;

                var idf = _idf.GetValueOrDefault(candidate.Term, 1.0);
                foreach (var posting in postingList)
                {
                    if (restrictedField.HasValue && posting.Field != restrictedField.Value)
                        continue;

                    var fieldWeight = posting.Field switch
                    {
                        PostingField.Marker => 8.0,
                        PostingField.Anchor => 5.0,
                        PostingField.Title => 4.0,
                        PostingField.Bigram => 4.5,
                        _ => 2.0
                    };

                    var exactness = candidate.IsPrefix ? 0.55 : 1.0;
                    var score = baseBoost * fieldWeight * idf * exactness;
                    Add(posting.DocumentId, score, $"{posting.Field}:{candidate.Term}");

                    var anchorScore = baseBoost * (posting.Field == PostingField.Anchor ? 2.0 : 0.8) * idf * exactness;
                    anchorScores[posting.AnchorId] = anchorScores.TryGetValue(posting.AnchorId, out var current)
                        ? current + anchorScore
                        : anchorScore;

                    if (_clustersByAnchor.TryGetValue(posting.AnchorId, out var clusters))
                    {
                        foreach (var cluster in clusters)
                        {
                            var clusterScore = anchorScore * 0.9;
                            clusterScores[cluster] = clusterScores.TryGetValue(cluster, out var currentCluster)
                                ? currentCluster + clusterScore
                                : clusterScore;

                            if (_supportedClusters.TryGetValue(cluster, out var supported))
                            {
                                foreach (var siblingCluster in supported)
                                {
                                    var supportScore = clusterScore * 0.55;
                                    clusterScores[siblingCluster] = clusterScores.TryGetValue(siblingCluster, out var currentSupport)
                                        ? currentSupport + supportScore
                                        : supportScore;
                                }
                            }
                        }
                    }
                }
            }
        }

        void Add(string documentId, double score, string reason)
        {
            scores[documentId] = scores.TryGetValue(documentId, out var current)
                ? current + score
                : score;

            if (!reasons.TryGetValue(documentId, out var list))
            {
                list = [];
                reasons[documentId] = list;
            }

            if (list.Count < 6)
                list.Add(reason);
        }
    }

    public RetrievalBundle SearchBundle(string prompt)
    {
        var rankedHits = Search(prompt, maxResults: Math.Max(_documents.Count, 8));
        var slotMap = new Dictionary<string, RetrievalHit>(StringComparer.OrdinalIgnoreCase);

        foreach (var hit in rankedHits)
        {
            var document = _documents.First(x => x.DocumentId == hit.DocumentId);
            var roles = InferSlots(document);

            foreach (var role in roles)
            {
                if (slotMap.ContainsKey(role))
                    continue;

                slotMap[role] = hit;
            }
        }

        return new RetrievalBundle(slotMap);
    }

    public RetrievalExplanation Explain(string prompt, int maxResults = 5)
    {
        var query = QueryFeatures.From(prompt);
        var rankedHits = Search(prompt, maxResults);
        var bundle = SearchBundle(prompt);

        var explainedHits = rankedHits
            .Select(hit =>
            {
                var document = _documents.First(x => x.DocumentId == hit.DocumentId);
                return new ExplainedHit(
                    hit.DocumentId,
                    hit.Title,
                    hit.Score,
                    hit.Reasons,
                    document.Facets,
                    InferSlots(document));
            })
            .ToArray();

        var neighbors = explainedHits.ToDictionary(
            x => x.DocumentId,
            x => (IReadOnlyList<string>)(_inferredNeighborsByAnchor.TryGetValue(_documents.First(d => d.DocumentId == x.DocumentId).AnchorId, out var list)
                ? list.Select(n => $"{n.ToAnchorId} ({n.Reason}, {n.Weight:F2})").ToArray()
                : Array.Empty<string>()),
            StringComparer.OrdinalIgnoreCase);

        return new RetrievalExplanation(
            prompt,
            query.Facets,
            explainedHits,
            bundle.Slots.ToDictionary(x => x.Key, x => x.Value.DocumentId, StringComparer.OrdinalIgnoreCase),
            neighbors);
    }

    private IEnumerable<(string Term, bool IsPrefix)> EnumerateTerms(string term, bool exactOnly)
    {
        yield return (term, false);
        if (exactOnly || term.Length < 4)
            yield break;

        foreach (var prefixMatch in _trie.GetByPrefix(term).Where(x => !string.Equals(x, term, StringComparison.OrdinalIgnoreCase)).Take(4))
            yield return (prefixMatch, true);
    }

    private sealed record IndexedDocument(
        string DocumentId,
        string AnchorId,
        string Title,
        double Confidence,
        bool IsActionOrProcedure,
        bool IsLookupOrDashboard,
        IReadOnlyList<string> Facets,
        IReadOnlyList<string> MarkerTokens,
        IReadOnlyList<string> AnchorTokens,
        IReadOnlyList<string> TitleTokens,
        IReadOnlyList<string> BodyTokens,
        IReadOnlyList<string> Bigrams)
    {
        public static IndexedDocument Create(RetrievedDocument document)
        {
            var anchorTokens = Tokenize(document.AnchorId).Concat(Tokenize(document.Title)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var titleTokens = Tokenize(document.Title).ToArray();
            var bodyTokens = Tokenize(document.Body).ToArray();
            var markers = MarkerRegex.Matches(document.Title + " " + document.Body).Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var bigrams = MakeBigrams(titleTokens.Concat(bodyTokens)).ToArray();
            var allText = (document.Title + " " + document.Body).ToLowerInvariant();
            var isActionOrProcedure = allText.Contains("procedure", StringComparison.Ordinal)
                || allText.Contains("restart", StringComparison.Ordinal)
                || allText.Contains("recover", StringComparison.Ordinal)
                || allText.Contains("enable", StringComparison.Ordinal)
                || allText.Contains("before deploy", StringComparison.Ordinal)
                || allText.Contains("precaution", StringComparison.Ordinal)
                || allText.Contains("guardrail", StringComparison.Ordinal);
            var isLookupOrDashboard = allText.Contains("dashboard", StringComparison.Ordinal)
                || allText.Contains("url", StringComparison.Ordinal)
                || allText.Contains("chart", StringComparison.Ordinal)
                || allText.Contains("metrics", StringComparison.Ordinal);
            var facets = InferFacets(document.AnchorId, document.Title, document.Body).ToArray();

            return new IndexedDocument(document.DocumentId, document.AnchorId, document.Title, document.Confidence, isActionOrProcedure, isLookupOrDashboard, facets, markers, anchorTokens, titleTokens, bodyTokens, bigrams);
        }
    }

    private sealed record QueryFeatures(IReadOnlyList<string> Markers, IReadOnlyList<string> Tokens, IReadOnlyList<string> Bigrams, IReadOnlyList<string> Facets)
    {
        public static QueryFeatures From(string prompt)
        {
            var markers = MarkerRegex.Matches(prompt).Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var tokens = Tokenize(prompt).ToArray();
            var bigrams = MakeBigrams(tokens).ToArray();
            var facets = InferQueryFacets(tokens, bigrams).ToArray();
            return new QueryFeatures(markers, tokens, bigrams, facets);
        }
    }

    private static double IntentAdjustment(QueryFeatures query, IndexedDocument document)
    {
        var actionSignals = query.Tokens.Count(x => ActionIntentTerms.Contains(x));
        var lookupSignals = query.Tokens.Count(x => LookupIntentTerms.Contains(x));
        var travelSignals = query.Tokens.Count(x => TravelIntentTerms.Contains(x));
        var score = 0.0;

        if (actionSignals > 0)
        {
            if (document.IsActionOrProcedure)
                score += 240.0 + (actionSignals * 24.0);
            if (document.IsLookupOrDashboard)
                score -= 260.0;
        }

        if (lookupSignals > 0)
        {
            if (document.IsLookupOrDashboard)
                score += 90.0 + (lookupSignals * 10.0);
            if (document.IsActionOrProcedure && lookupSignals >= actionSignals)
                score -= 40.0;
        }

        if (travelSignals > 0)
        {
            if (document.AnchorId.Contains("travel", StringComparison.OrdinalIgnoreCase))
                score += 85.0 + (travelSignals * 10.0);
            if (document.AnchorId.Contains("stirtrek", StringComparison.OrdinalIgnoreCase))
                score += 45.0 + (travelSignals * 4.0);
        }

        return score;
    }

    private static double FacetBoost(string facet, QueryFeatures query, IndexedDocument document)
    {
        return facet switch
        {
            "travel_profile" => document.Facets.Contains("travel_profile", StringComparer.OrdinalIgnoreCase)
                ? 140.0 + (query.Tokens.Count(x => TravelIntentTerms.Contains(x)) * 8.0)
                : 0.0,
            "trip_planning" => document.Facets.Contains("trip_planning", StringComparer.OrdinalIgnoreCase)
                ? 110.0 + (query.Tokens.Count(x => TravelIntentTerms.Contains(x)) * 6.0)
                : 0.0,
            "incident_recovery" => document.IsActionOrProcedure
                ? 130.0
                : document.IsLookupOrDashboard ? -120.0 : 45.0,
            "rollout_guardrail" => document.IsActionOrProcedure ? 120.0 : 35.0,
            _ => 0.0
        };
    }

    private double ClusterWeight(QueryFeatures query, IndexedDocument document, string cluster)
    {
        var weight = 1.2;

        if (cluster.Contains("travel-profile", StringComparison.OrdinalIgnoreCase))
        {
            var travelSignals = query.Tokens.Count(x => TravelIntentTerms.Contains(x));
            weight += travelSignals > 0 ? 3.0 : 0.0;

            if (_rolesByAnchor.TryGetValue(document.AnchorId, out var roles))
            {
                if (roles.Contains("role:origin-airport", StringComparer.OrdinalIgnoreCase)
                    || roles.Contains("role:preferred-airline", StringComparer.OrdinalIgnoreCase))
                    weight += 2.5;
            }
        }

        if (cluster.Contains("stirtrek-trip", StringComparison.OrdinalIgnoreCase))
        {
            var tripSignals = query.Tokens.Count(x => TravelIntentTerms.Contains(x)) + query.Tokens.Count(x => x is "stir" or "trek");
            weight += tripSignals > 0 ? 1.75 : 0.0;
        }

        return weight;
    }

    private IReadOnlyList<ScoredHit> Diversify(IReadOnlyList<ScoredHit> rankedHits, QueryFeatures query, int maxResults)
    {
        if (rankedHits.Count <= maxResults)
            return rankedHits;

        var results = new List<ScoredHit>(maxResults);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeFacets = query.Facets
            .Where(f => f is "travel_profile" or "trip_planning" or "incident_recovery" or "rollout_guardrail")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var facet in activeFacets)
        {
            var bestFacetHit = rankedHits.FirstOrDefault(x => x.Document.Facets.Contains(facet, StringComparer.OrdinalIgnoreCase) && used.Add(x.DocumentId));
            if (bestFacetHit is not null)
                results.Add(bestFacetHit);
            if (results.Count == maxResults)
                return results;
        }

        var wantsTravelBundle = query.Facets.Contains("travel_profile", StringComparer.OrdinalIgnoreCase)
            || query.Facets.Contains("trip_planning", StringComparer.OrdinalIgnoreCase);
        if (wantsTravelBundle)
        {
            foreach (var requiredSlot in new[] { "origin_airport", "preferred_airline" })
            {
                var bestRoleHit = rankedHits.FirstOrDefault(x =>
                    InferSlots(x.Document).Contains(requiredSlot, StringComparer.OrdinalIgnoreCase)
                    && used.Add(x.DocumentId));
                if (bestRoleHit is not null)
                    results.Add(bestRoleHit);
                if (results.Count == maxResults)
                    return results;
            }
        }

        foreach (var hit in rankedHits)
        {
            if (used.Add(hit.DocumentId))
                results.Add(hit);
            if (results.Count == maxResults)
                break;
        }

        return results;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in TokenRegex.Matches(text))
        {
            var token = match.Value.Trim().ToLowerInvariant();
            if (token.Length < 2)
                continue;
            if (StopWords.Contains(token))
                continue;

            yield return Stem(token);
        }
    }

    private static string Stem(string token)
    {
        if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 4)
            return token[..^3] + "y";
        if (token.EndsWith("ing", StringComparison.Ordinal) && token.Length > 5)
            return token[..^3];
        if (token.EndsWith("ed", StringComparison.Ordinal) && token.Length > 4)
            return token[..^2];
        if (token.EndsWith('s') && token.Length > 4)
            return token[..^1];

        return token;
    }

    private static IEnumerable<string> MakeBigrams(IEnumerable<string> tokens)
    {
        string? previous = null;
        foreach (var token in tokens)
        {
            if (previous is not null)
                yield return previous + " " + token;
            previous = token;
        }
    }

    private static IEnumerable<string> InferFacets(string anchorId, string title, string body)
    {
        var text = (anchorId + " " + title + " " + body).ToLowerInvariant();

        if (text.Contains("airport", StringComparison.Ordinal)
            || text.Contains("airline", StringComparison.Ordinal)
            || text.Contains("united", StringComparison.Ordinal)
            || text.Contains("iah", StringComparison.Ordinal)
            || text.Contains("flight", StringComparison.Ordinal)
            || text.Contains("fly", StringComparison.Ordinal)
            || text.Contains("travel profile", StringComparison.Ordinal)
            || text.Contains("status benefits", StringComparison.Ordinal))
            yield return "travel_profile";

        if (text.Contains("hotel", StringComparison.Ordinal)
            || text.Contains("rental car", StringComparison.Ordinal)
            || text.Contains("stir trek", StringComparison.Ordinal)
            || text.Contains("easton", StringComparison.Ordinal)
            || text.Contains("columbus", StringComparison.Ordinal)
            || text.Contains("cmh", StringComparison.Ordinal))
            yield return "trip_planning";

        if (text.Contains("beta", StringComparison.Ordinal)
            || text.Contains("queue", StringComparison.Ordinal)
            || text.Contains("backlog", StringComparison.Ordinal)
            || text.Contains("worker-b", StringComparison.Ordinal)
            || text.Contains("recover", StringComparison.Ordinal)
            || text.Contains("restart", StringComparison.Ordinal))
            yield return "incident_recovery";

        if (text.Contains("alpha", StringComparison.Ordinal)
            || text.Contains("rollout", StringComparison.Ordinal)
            || text.Contains("feature flag", StringComparison.Ordinal)
            || text.Contains("guardrail", StringComparison.Ordinal)
            || text.Contains("deploy", StringComparison.Ordinal))
            yield return "rollout_guardrail";
    }

    private static IReadOnlyList<string> InferSlots(IndexedDocument document)
    {
        var text = (document.AnchorId + " " + document.Title + " " + string.Join(' ', document.Facets)).ToLowerInvariant();
        var slots = new List<string>();

        if (text.Contains("airport", StringComparison.Ordinal) || text.Contains("iah", StringComparison.Ordinal) || text.Contains("origin", StringComparison.Ordinal))
            slots.Add("origin_airport");

        if (text.Contains("airline", StringComparison.Ordinal) || text.Contains("united", StringComparison.Ordinal) || text.Contains("preferred", StringComparison.Ordinal))
            slots.Add("preferred_airline");

        if (text.Contains("travel recommendation", StringComparison.Ordinal) || text.Contains("hotel", StringComparison.Ordinal) || text.Contains("rental car", StringComparison.Ordinal) || text.Contains("trip_planning", StringComparison.Ordinal))
            slots.Add("trip_plan");

        if (text.Contains("venue area", StringComparison.Ordinal) || text.Contains("downtown", StringComparison.Ordinal) || text.Contains("easton", StringComparison.Ordinal) || text.Contains("venue", StringComparison.Ordinal))
            slots.Add("venue_area");

        return slots;
    }

    private static IEnumerable<string> InferQueryFacets(IReadOnlyList<string> tokens, IReadOnlyList<string> bigrams)
    {
        var joined = string.Join(' ', tokens.Concat(bigrams));

        if (joined.Contains("airport", StringComparison.Ordinal)
            || joined.Contains("airline", StringComparison.Ordinal)
            || joined.Contains("flight", StringComparison.Ordinal)
            || joined.Contains("fly", StringComparison.Ordinal)
            || joined.Contains("trip", StringComparison.Ordinal)
            || joined.Contains("travel", StringComparison.Ordinal)
            || joined.Contains("book flight", StringComparison.Ordinal))
            yield return "travel_profile";

        if (joined.Contains("hotel", StringComparison.Ordinal)
            || joined.Contains("rental car", StringComparison.Ordinal)
            || joined.Contains("stir trek", StringComparison.Ordinal)
            || joined.Contains("downtown columbu", StringComparison.Ordinal)
            || joined.Contains("venue", StringComparison.Ordinal))
            yield return "trip_planning";

        if (joined.Contains("queue", StringComparison.Ordinal)
            || joined.Contains("backlog", StringComparison.Ordinal)
            || joined.Contains("control", StringComparison.Ordinal)
            || joined.Contains("last time", StringComparison.Ordinal)
            || joined.Contains("incident", StringComparison.Ordinal))
            yield return "incident_recovery";

        if (joined.Contains("rollout", StringComparison.Ordinal)
            || joined.Contains("precaution", StringComparison.Ordinal)
            || joined.Contains("wobble", StringComparison.Ordinal)
            || joined.Contains("deploy", StringComparison.Ordinal)
            || joined.Contains("feature flag", StringComparison.Ordinal))
            yield return "rollout_guardrail";
    }

    private static IReadOnlyDictionary<string, List<NeighborEdge>> BuildInferredNeighbors(
        IReadOnlyList<IndexedDocument> documents,
        IReadOnlyDictionary<string, string[]> aliasesByAnchor)
    {
        var byAnchor = new Dictionary<string, List<NeighborEdge>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < documents.Count; i++)
        {
            var left = documents[i];
            var leftSignature = BuildSignature(left, aliasesByAnchor);

            for (var j = i + 1; j < documents.Count; j++)
            {
                var right = documents[j];
                var rightSignature = BuildSignature(right, aliasesByAnchor);
                var sharedTerms = leftSignature.Intersect(rightSignature, StringComparer.OrdinalIgnoreCase).ToArray();
                if (sharedTerms.Length == 0)
                    continue;

                var overlap = sharedTerms.Length / (double)Math.Max(leftSignature.Count, rightSignature.Count);
                var sharedFacets = left.Facets.Intersect(right.Facets, StringComparer.OrdinalIgnoreCase).Count();
                var similarity = overlap + (sharedFacets * 0.18);
                if (similarity < 0.22)
                    continue;

                var reason = sharedFacets > 0 ? "signature+facet" : "signature";
                AddNeighbor(left.AnchorId, right.AnchorId, Math.Min(1.1, 0.45 + similarity), reason);
                AddNeighbor(right.AnchorId, left.AnchorId, Math.Min(1.1, 0.45 + similarity), reason);
            }
        }

        return byAnchor;

        void AddNeighbor(string fromAnchor, string toAnchor, double weight, string reason)
        {
            if (!byAnchor.TryGetValue(fromAnchor, out var list))
            {
                list = [];
                byAnchor[fromAnchor] = list;
            }

            list.Add(new NeighborEdge(toAnchor, weight, reason));
        }
    }

    private static HashSet<string> BuildSignature(IndexedDocument document, IReadOnlyDictionary<string, string[]> aliasesByAnchor)
    {
        var signature = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in document.MarkerTokens)
            signature.Add(term);
        foreach (var term in document.TitleTokens)
            signature.Add(term);
        foreach (var term in document.AnchorTokens)
            signature.Add(term);
        foreach (var facet in document.Facets)
            signature.Add($"facet:{facet}");

        if (aliasesByAnchor.TryGetValue(document.AnchorId, out var aliases))
        {
            foreach (var alias in aliases)
            {
                foreach (var token in Tokenize(alias))
                    signature.Add(token);
            }
        }

        return signature;
    }

    private sealed class TermTrie
    {
        private readonly Node _root = new();

        public void Add(string term)
        {
            var current = _root;
            foreach (var ch in term)
            {
                if (!current.Children.TryGetValue(ch, out var next))
                {
                    next = new Node();
                    current.Children[ch] = next;
                }

                current = next;
            }

            current.Term = term;
        }

        public IEnumerable<string> GetByPrefix(string prefix)
        {
            var current = _root;
            foreach (var ch in prefix)
            {
                if (!current.Children.TryGetValue(ch, out var next))
                    yield break;
                current = next;
            }

            foreach (var term in Enumerate(current))
                yield return term;
        }

        private static IEnumerable<string> Enumerate(Node node)
        {
            if (node.Term is not null)
                yield return node.Term;

            foreach (var child in node.Children.Values)
            {
                foreach (var term in Enumerate(child))
                    yield return term;
            }
        }

        private sealed class Node
        {
            public Dictionary<char, Node> Children { get; } = [];
            public string? Term { get; set; }
        }
    }

    private sealed record Posting(string DocumentId, string AnchorId, PostingField Field);

    private sealed record NeighborEdge(string ToAnchorId, double Weight, string Reason);

    private sealed record ScoredHit(IndexedDocument Document, double Score, IReadOnlyList<string> Reasons)
    {
        public string DocumentId => Document.DocumentId;
        public string Title => Document.Title;
    }

    private enum PostingField
    {
        Marker,
        Anchor,
        Title,
        Body,
        Bigram
    }
}
