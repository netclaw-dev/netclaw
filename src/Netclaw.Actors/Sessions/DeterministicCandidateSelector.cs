using Netclaw.Actors.Memory;
using Netclaw.Actors.Text;

namespace Netclaw.Actors.Sessions;

public sealed class DeterministicCandidateSelector
{

    public IReadOnlyList<SQLiteMemoryHydratedItem> Select(
        DeterministicRetrievalRequestPlan plan,
        IReadOnlyList<SQLiteMemoryHydratedItem> documents)
    {
        return documents
            .Where(d => plan.AllowedMemoryClasses.Contains(d.MemoryClass, StringComparer.OrdinalIgnoreCase))
            .Where(d => !plan.ExcludedSensitivity.Contains(d.Sensitivity, StringComparer.OrdinalIgnoreCase))
            .Select(d => new { Document = d, Score = Score(plan, d) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Document.Id, StringComparer.Ordinal)
            .Take(plan.CandidateLimit)
            .Select(x => x.Document)
            .ToArray();
    }

    private static double Score(DeterministicRetrievalRequestPlan plan, SQLiteMemoryHydratedItem document)
    {
        var score = 0.0;
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

        return score;
    }

}
