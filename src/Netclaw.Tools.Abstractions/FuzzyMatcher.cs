using System.Linq;

namespace Netclaw.Tools
{
    public static class FuzzyMatcher
    {
        public static string? FindBestMatch(string input, System.Collections.Generic.IEnumerable<string> candidates, double threshold = 0.75)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var normalisedInput = Normalise(input);
            if (normalisedInput.Length == 0) return null;

            string? bestMatch = null;
            double bestScore = 0;

            foreach (var candidate in candidates)
            {
                var normalisedCandidate = Normalise(candidate);
                if (normalisedCandidate.Length == 0) continue;
                double score = Similarity(normalisedInput, normalisedCandidate);
                if (normalisedCandidate.Contains(normalisedInput, StringComparison.Ordinal)) score = System.Math.Max(score, 0.85);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = candidate;
                }
            }

            return bestScore >= threshold ? bestMatch : null;
        }

        private static string Normalise(string value) =>
            new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private static double Similarity(string a, string b)
        {
            int distance = LevenshteinDistance(a, b);
            return 1.0 - (double)distance / System.Math.Max(a.Length, b.Length);
        }

        private static int LevenshteinDistance(string a, string b)
        {
            var previous = System.Linq.Enumerable.Range(0, b.Length + 1).ToArray();
            var current = new int[b.Length + 1];
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = System.Math.Min(System.Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                (previous, current) = (current, previous);
            }
            return previous[b.Length];
        }
    }
}
