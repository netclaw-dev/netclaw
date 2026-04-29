// -----------------------------------------------------------------------
// <copyright file="CandidateSelector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.MemoryRetrievalPoC.Tests.Prototype;

internal sealed class CandidateSelector
{
    private static readonly Regex TokenRegex = new("[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "about", "are", "at", "be", "for", "from", "how", "i", "if", "in", "is", "it", "of", "on", "or", "the", "to", "what", "when", "where", "with", "you"
    ];

    public IReadOnlyList<RetrievedDocument> Select(RetrievalRequestPlan plan, IReadOnlyList<RetrievedDocument> documents)
    {
        var ranked = documents
            .Where(d => plan.AllowedMemoryClasses.Contains(d.MemoryClass, StringComparer.OrdinalIgnoreCase))
            .Where(d => !plan.ExcludedSensitivity.Contains(d.Sensitivity, StringComparer.OrdinalIgnoreCase))
            .Select(d => new
            {
                Document = d,
                Score = CandidateScore(plan, d)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Document.DocumentId, StringComparer.Ordinal)
            .Take(plan.CandidateLimit)
            .Select(x => x.Document)
            .ToArray();

        return ranked;
    }

    private static double CandidateScore(RetrievalRequestPlan plan, RetrievedDocument document)
    {
        var score = 0.0;
        var text = (document.CanonicalName + " " + document.Title + " " + document.Body).ToLowerInvariant();
        var tokens = Tokenize(text).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var term in plan.LexicalTerms)
        {
            if (tokens.Contains(term))
                score += 4.0;
        }

        foreach (var facet in plan.Facets)
        {
            if (text.Contains(facet.Replace('_', ' '), StringComparison.OrdinalIgnoreCase))
                score += 6.0;
        }

        foreach (var anchor in plan.AnchorHints)
        {
            if (string.Equals(document.AnchorId, anchor, StringComparison.OrdinalIgnoreCase))
                score += 18.0;
            else if (document.CanonicalName.Contains(anchor.Replace("anchor:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                score += 8.0;
        }

        foreach (var scope in plan.SoftScopes)
        {
            if (text.Contains(scope.Replace("scope:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase)
                || document.AnchorId.Contains(scope.Replace("project:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                score += 3.5;
        }

        return score + document.Confidence;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in TokenRegex.Matches(text))
        {
            var token = match.Value.ToLowerInvariant();
            if (token.Length < 2 || StopWords.Contains(token))
                continue;
            yield return token;
        }
    }
}
