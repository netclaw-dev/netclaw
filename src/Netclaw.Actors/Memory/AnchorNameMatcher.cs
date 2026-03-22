namespace Netclaw.Actors.Memory;

/// <summary>
/// Utility class for fuzzy matching anchor names by tokenizing on '-'
/// and comparing token sets using Jaccard similarity with subset matching.
/// </summary>
public static class AnchorNameMatcher
{
    private const double JaccardThreshold = 0.6;
    private const int MaxTokenDifference = 1;

    /// <summary>
    /// Tokenize an anchor name by splitting on '-' and lowering.
    /// </summary>
    public static string[] Tokenize(string anchorName)
    {
        if (string.IsNullOrWhiteSpace(anchorName))
            return [];

        return anchorName
            .Trim()
            .ToLowerInvariant()
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Check if two token arrays are a fuzzy match.
    /// Two anchors are fuzzy matches if:
    /// - Jaccard similarity >= 0.6
    /// - AND (the shorter is a subset of the longer, OR they differ by at most 1 token)
    /// </summary>
    public static bool IsFuzzyMatch(string[] tokensA, string[] tokensB)
    {
        if (tokensA.Length == 0 || tokensB.Length == 0)
            return false;

        var setA = new HashSet<string>(tokensA, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(tokensB, StringComparer.OrdinalIgnoreCase);

        var intersection = new HashSet<string>(setA, StringComparer.OrdinalIgnoreCase);
        intersection.IntersectWith(setB);

        var union = new HashSet<string>(setA, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(setB);

        if (union.Count == 0)
            return false;

        var jaccard = (double)intersection.Count / union.Count;
        if (jaccard < JaccardThreshold)
            return false;

        // Check subset: all tokens of the shorter set are in the longer set
        var shorter = setA.Count <= setB.Count ? setA : setB;
        var longer = setA.Count <= setB.Count ? setB : setA;
        var isSubset = shorter.All(t => longer.Contains(t));

        if (isSubset)
            return true;

        // Check token difference: symmetric difference <= 1
        var symmetricDifference = union.Count - intersection.Count;
        return symmetricDifference <= MaxTokenDifference;
    }

    /// <summary>
    /// Find all existing anchor names that fuzzy-match the proposed name.
    /// </summary>
    public static IReadOnlyList<string> FindFuzzyMatches(
        string proposedName,
        IReadOnlyList<string> existingNames)
    {
        var proposedTokens = Tokenize(proposedName);
        if (proposedTokens.Length == 0)
            return [];

        var matches = new List<string>();
        foreach (var existing in existingNames)
        {
            var existingTokens = Tokenize(existing);
            if (IsFuzzyMatch(proposedTokens, existingTokens))
            {
                matches.Add(existing);
            }
        }

        return matches;
    }

    /// <summary>
    /// Compute token-level Jaccard similarity between two content strings.
    /// Tokens are whitespace-split, lowered words.
    /// </summary>
    public static double ComputeContentOverlap(string contentA, string contentB)
    {
        if (string.IsNullOrWhiteSpace(contentA) || string.IsNullOrWhiteSpace(contentB))
            return 0.0;

        var tokensA = TokenizeContent(contentA);
        var tokensB = TokenizeContent(contentB);

        if (tokensA.Count == 0 || tokensB.Count == 0)
            return 0.0;

        var intersection = new HashSet<string>(tokensA, StringComparer.OrdinalIgnoreCase);
        intersection.IntersectWith(tokensB);

        var union = new HashSet<string>(tokensA, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(tokensB);

        return union.Count == 0 ? 0.0 : (double)intersection.Count / union.Count;
    }

    private static HashSet<string> TokenizeContent(string content)
    {
        var words = content
            .Split([' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new HashSet<string>(
            words.Select(w => w.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }
}
