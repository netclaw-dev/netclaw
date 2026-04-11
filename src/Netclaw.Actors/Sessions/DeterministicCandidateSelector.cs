using Netclaw.Actors.Memory;
using Netclaw.Actors.Text;

namespace Netclaw.Actors.Sessions;

public sealed class DeterministicCandidateSelector
{
    private const double BaselineScore = 1.0;
    private const double MinimumSelectorScore = 2.0;
    private const double LexicalMatchWeight = 4.0;
    private const double FacetMatchWeight = 6.0;
    private const double AnchorMatchWeight = 8.0;
    private const double SoftScopeWeight = 3.5;

    public IReadOnlyList<SQLiteMemoryHydratedItem> Select(
        DeterministicRetrievalRequestPlan plan,
        IReadOnlyList<SQLiteMemoryHydratedItem> documents)
        => SelectWithScores(plan, documents).Select(x => x.Item).ToArray();

    public IReadOnlyList<ScoredCandidate> SelectWithScores(
        DeterministicRetrievalRequestPlan plan,
        IReadOnlyList<SQLiteMemoryHydratedItem> documents)
    {
        return documents
            .Where(d => plan.AllowedMemoryClasses.Contains(d.MemoryClass, StringComparer.OrdinalIgnoreCase))
            .Where(d => !plan.ExcludedSensitivity.Contains(d.Sensitivity, StringComparer.OrdinalIgnoreCase))
            .Select(d => new ScoredCandidate(d, Score(plan, d)))
            .Where(x => x.SelectorScore >= MinimumSelectorScore)
            .OrderByDescending(x => x.SelectorScore)
            .ThenBy(x => x.Item.Id, StringComparer.Ordinal)
            .Take(plan.CandidateLimit)
            .ToArray();
    }

    public sealed record ScoredCandidate(SQLiteMemoryHydratedItem Item, double SelectorScore);

    private static double Score(DeterministicRetrievalRequestPlan plan, SQLiteMemoryHydratedItem document)
    {
        // Baseline: candidates survived SQL pre-filtering (FTS match), so they
        // deserve a non-zero score. Lexical/facet/anchor matches boost above this.
        var score = BaselineScore;

        // Build combined text once; skip ToLowerInvariant here since
        // TextTokenizer.Tokenize already lowercases internally and the
        // HashSet uses OrdinalIgnoreCase.
        var text = document.Title + " " + document.Content + " " + (document.AliasesJson ?? string.Empty) + " " + (document.FacetsJson ?? string.Empty);
        var tokens = TextTokenizer.Tokenize(text).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var term in plan.LexicalTerms)
            if (tokens.Contains(term))
                score += LexicalMatchWeight;

        foreach (var facet in plan.Facets)
            if ((document.FacetsJson ?? string.Empty).Contains(facet, StringComparison.OrdinalIgnoreCase))
                score += FacetMatchWeight;

        foreach (var anchor in plan.AnchorHints)
            if (document.Title.Contains(anchor, StringComparison.OrdinalIgnoreCase)
                || document.Content.Contains(anchor, StringComparison.OrdinalIgnoreCase)
                || (document.AliasesJson ?? string.Empty).Contains(anchor, StringComparison.OrdinalIgnoreCase))
                score += AnchorMatchWeight;

        foreach (var scope in plan.SoftScopes)
            if (text.Contains(scope.Replace("scope:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                score += SoftScopeWeight;

        return score;
    }

}
