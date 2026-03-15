using System.Text.RegularExpressions;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Text;

namespace Netclaw.Actors.Sessions;

public enum DeterministicRetrievalMode
{
    Ranked,
    Bundle
}

public sealed record DeterministicRetrievalRequestPlan(
    string HardScope,
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
        var hardScope = ResolveHardScope(request);
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
            HardScope: hardScope,
            SoftScopes: softScopes,
            RetrievalMode: retrievalMode,
            LexicalTerms: tokens,
            Facets: facets,
            AnchorHints: anchorHints,
            CandidateLimit: retrievalMode == DeterministicRetrievalMode.Bundle ? 60 : 30,
            AllowedMemoryClasses: [MemoryClass.DurableFact.ToWireValue()],
            ExcludedSensitivity: [MemorySensitivity.Secret.ToWireValue()],
            ExcludeExpired: true);
    }

    public static string ResolveHardScope(AutomaticRecallRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.HardScopeOverride))
            return request.HardScopeOverride!;

        var sessionId = request.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return "project:default";

        var slash = sessionId.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return "project:default";

        var prefix = sessionId[..slash].Trim();
        return string.IsNullOrWhiteSpace(prefix)
            ? "project:default"
            : $"project:{prefix.ToLowerInvariant()}";
    }

    private static IEnumerable<string> InferAnchorHints(AutomaticRecallRequest request, string prompt, IReadOnlyList<string> tokens)
    {
        foreach (var entity in request.RecentEntities ?? [])
            if (!string.IsNullOrWhiteSpace(entity))
                yield return entity.Trim();

        foreach (Match match in Regex.Matches(prompt, "\\b[A-Z][A-Za-z0-9._-]{2,}\\b"))
            yield return match.Value;

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

    private static DeterministicRetrievalMode InferMode(IReadOnlyList<string> tokens, IReadOnlyList<string> facets)
    {
        var wantsBundle = facets.Contains("trip_planning")
            || (facets.Contains("travel_profile") && tokens.Any(x => x is "what" or "which" or "book" or "best"));

        return wantsBundle ? DeterministicRetrievalMode.Bundle : DeterministicRetrievalMode.Ranked;
    }

}
