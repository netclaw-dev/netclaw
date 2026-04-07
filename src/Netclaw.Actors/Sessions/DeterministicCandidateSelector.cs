using Netclaw.Actors.Memory;
using Netclaw.Actors.Text;

namespace Netclaw.Actors.Sessions;

public sealed class DeterministicCandidateSelector
{

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
            .Where(x => x.SelectorScore > 0)
            .OrderByDescending(x => x.SelectorScore)
            .ThenBy(x => x.Item.Id, StringComparer.Ordinal)
            .Take(plan.CandidateLimit)
            .ToArray();
    }

    public sealed record ScoredCandidate(SQLiteMemoryHydratedItem Item, double SelectorScore);

    private static double Score(DeterministicRetrievalRequestPlan plan, SQLiteMemoryHydratedItem document)
    {
        // Baseline: candidates survived SQL pre-filtering (LIKE match), so they
        // deserve a non-zero score. Lexical/facet/anchor matches boost above this.
        var score = 1.0;
        var text = (document.Title + " " + document.Content + " " + (document.AliasesJson ?? string.Empty) + " " + (document.FacetsJson ?? string.Empty)).ToLowerInvariant();
        var tokens = TextTokenizer.Tokenize(text).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var term in plan.LexicalTerms)
            if (tokens.Contains(term))
                score += 4.0;

        foreach (var facet in plan.Facets)
            if ((document.FacetsJson ?? string.Empty).Contains(facet, StringComparison.OrdinalIgnoreCase))
                score += 6.0;

        foreach (var anchor in plan.AnchorHints)
            if (document.Title.Contains(anchor, StringComparison.OrdinalIgnoreCase)
                || document.Content.Contains(anchor, StringComparison.OrdinalIgnoreCase)
                || (document.AliasesJson ?? string.Empty).Contains(anchor, StringComparison.OrdinalIgnoreCase))
                score += 8.0;

        foreach (var scope in plan.SoftScopes)
            if (text.Contains(scope.Replace("scope:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                score += 3.5;

        // Domain affinity: same-domain memories rank higher but cross-domain
        // memories aren't excluded (audience+boundary are the security gates).
        if (string.Equals(document.Domain, plan.HardScope, StringComparison.OrdinalIgnoreCase))
            score += 5.0;

        return score;
    }

}
