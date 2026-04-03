namespace Netclaw.Actors.Memory;

/// <summary>
/// Utility class for fuzzy matching anchor names by tokenizing on '-'
/// and comparing token sets using Jaccard similarity with subset matching.
/// </summary>
public static class AnchorNameMatcher
{
    private const double JaccardThreshold = 0.6;
    private const int MaxTokenDifference = 2;
    private const int MinPrefixLength = 3;

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
    /// Compute Jaccard similarity between two sets of anchor tokens,
    /// using prefix-aware matching (one token is a prefix of the other, min 3 chars).
    /// </summary>
    public static double ComputeAnchorJaccard(string[] tokensA, string[] tokensB)
    {
        if (tokensA.Length == 0 || tokensB.Length == 0)
            return 0.0;

        var setA = new HashSet<string>(tokensA, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(tokensB, StringComparer.OrdinalIgnoreCase);

        return ComputeSoftJaccard(setA, setB);
    }

    /// <summary>
    /// Check if two token arrays are a fuzzy match.
    /// Two anchors are fuzzy matches if:
    /// - Jaccard similarity >= 0.6 (using prefix-aware token matching)
    /// - AND (the shorter is a soft-subset of the longer, OR they differ by at most 2 tokens)
    /// </summary>
    public static bool IsFuzzyMatch(string[] tokensA, string[] tokensB)
    {
        if (tokensA.Length == 0 || tokensB.Length == 0)
            return false;

        var setA = new HashSet<string>(tokensA, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(tokensB, StringComparer.OrdinalIgnoreCase);

        var (jaccard, softIntersection, softUnion) = ComputeSoftJaccardDetail(setA, setB);
        if (jaccard < JaccardThreshold)
            return false;

        // Check soft-subset: all tokens of the shorter set prefix-match something in the longer set
        var shorter = setA.Count <= setB.Count ? setA : setB;
        var longer = setA.Count <= setB.Count ? setB : setA;
        var isSoftSubset = shorter.All(t => longer.Any(l => IsPrefixMatch(t, l)));

        if (isSoftSubset)
            return true;

        // Check token difference: unmatched tokens <= MaxTokenDifference
        var unmatchedCount = softUnion - softIntersection;
        return unmatchedCount <= MaxTokenDifference;
    }

    private static double ComputeSoftJaccard(HashSet<string> setA, HashSet<string> setB)
    {
        var (jaccard, _, _) = ComputeSoftJaccardDetail(setA, setB);
        return jaccard;
    }

    /// <summary>
    /// Compute prefix-aware Jaccard similarity, returning the score plus the raw
    /// soft-intersection and soft-union counts for callers that need them.
    /// </summary>
    private static (double Jaccard, int SoftIntersection, int SoftUnion) ComputeSoftJaccardDetail(
        HashSet<string> setA, HashSet<string> setB)
    {
        var rawCount = 0;
        foreach (var a in setA)
        {
            if (setB.Any(b => IsPrefixMatch(a, b)))
                rawCount++;
        }

        // Cap at min(|A|, |B|) to prevent many-to-one prefix matches
        // (e.g., {"repo", "repos"} both matching "repository") from inflating
        // the intersection beyond what inclusion-exclusion allows.
        var softIntersection = Math.Min(rawCount, Math.Min(setA.Count, setB.Count));
        var softUnion = setA.Count + setB.Count - softIntersection;
        var jaccard = softUnion == 0 ? 0.0 : (double)softIntersection / softUnion;

        return (jaccard, softIntersection, softUnion);
    }

    /// <summary>
    /// Two tokens match if they are equal (case-insensitive) or one is a prefix
    /// of the other with minimum prefix length of 3 characters.
    /// </summary>
    private static bool IsPrefixMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;

        // The shorter string must be at least MinPrefixLength chars to count as a prefix
        var shorter = a.Length <= b.Length ? a : b;
        var longer = a.Length <= b.Length ? b : a;

        return shorter.Length >= MinPrefixLength
            && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
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
