// -----------------------------------------------------------------------
// <copyright file="MergeGuard.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Result of a single <see cref="MergeGuard.Validate"/> call.
/// </summary>
public sealed record MergeGuardResult(bool Passed, IReadOnlyList<string> MissingTokens, string Reason);

/// <summary>
/// Deterministic validator for LLM-synthesized merge bodies (memory-core-redesign design
/// D5). The curation LLM occasionally drops information when combining several source
/// documents into one — the May 2026 decider eval measured ~27% wrong-merge on hard
/// near-duplicates. Trusting the merge blindly risks silent, unrecoverable data loss;
/// refusing to merge at all just recreates the duplicate-accumulation problem curation
/// exists to fix. This guard turns a bad merge into a <b>recoverable</b> state instead: on
/// failure, <see cref="MemoryCurationEvaluator.ApplyDecisionAsync"/> falls back to a
/// structural append, so every source's content survives even though the synthesis didn't
/// land — over-appending is the acceptable failure mode, silent loss is not.
///
/// Two independent checks, both must pass:
/// 1. <b>Retention</b> — every load-bearing token (URL; number/version/quantity/date;
///    camelCase/snake_case/kebab-case/dotted.path/ALL_CAPS identifier; file path) extracted
///    from ANY source body must be case-insensitively present in the merged body, for at
///    least 95% of the token union across all sources. The 5% slack tolerates trivial LLM
///    rewording of genuinely incidental tokens (e.g. a URL repeated verbatim in two sources).
/// 2. <b>Collapse</b> — the merged body must be at least 60% as long as the longest single
///    source. Catches an LLM that "merges" by discarding everything but a short summary,
///    which could otherwise pass the retention check if the summary happens to repeat every
///    load-bearing token without preserving the surrounding prose.
///
/// Pure function, no I/O — safe to property-test with generated source/merged bodies.
/// </summary>
public static class MergeGuard
{
    private const double RetentionThreshold = 0.95;
    private const double LengthCollapseThreshold = 0.60;

    private const string MonthNames =
        "Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|" +
        "Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?";

    // URLs (trailing sentence punctuation is trimmed in ExtractLoadBearingTokens below).
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>\)\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ISO-8601 dates, with or without a time component: 2026-05-13, 2026-05-13T10:00:00Z.
    private static readonly Regex IsoDatePattern = new(
        @"\b\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}(?::\d{2})?Z?)?\b", RegexOptions.Compiled);

    // Slash-separated dates: 05/13/2026, 5/13/26.
    private static readonly Regex SlashDatePattern = new(
        @"\b\d{1,2}/\d{1,2}/\d{2,4}\b", RegexOptions.Compiled);

    // Written dates in either order: "May 13, 2026" / "13 May 2026".
    private static readonly Regex WrittenDatePattern = new(
        $@"\b(?:{MonthNames})\.?\s+\d{{1,2}}(?:st|nd|rd|th)?,?\s+\d{{4}}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReverseWrittenDatePattern = new(
        $@"\b\d{{1,2}}\s+(?:{MonthNames})\.?,?\s+\d{{4}}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Dotted/multi-segment versions: 1.2.3, 1.5.62, 10.0.
    private static readonly Regex VersionPattern = new(
        @"\b\d+(?:\.\d+){1,3}\b", RegexOptions.Compiled);

    // Quantities: a number immediately followed by a short unit — 64GB, 300ms, 72h, 10s.
    private static readonly Regex QuantityPattern = new(
        @"\b\d+(?:\.\d+)?[a-zA-Z]{1,6}\b", RegexOptions.Compiled);

    // Bare integers not already part of a version, quantity, or identifier: the lookarounds
    // exclude a digit preceded by "<digit>." (the "62" inside "1.5.62") or a letter/underscore
    // (mid-identifier), and a digit followed by a letter/underscore (the "20" inside "20GB")
    // or ".<digit>" (the "1" inside "1.5"). Ordinary sentence punctuation immediately after a
    // number — "cost is 111." — is deliberately NOT excluded here, unlike a naive
    // "no dot allowed after" rule would: a trailing period with no digit after it is not part
    // of a version/decimal, so the number is still load-bearing and must be captured.
    private static readonly Regex BareIntegerPattern = new(
        @"(?<![a-zA-Z_])(?<!\d\.)\d+(?![a-zA-Z_])(?!\.\d)", RegexOptions.Compiled);

    // File paths: at least one path separator plus a final dotted extension.
    private static readonly Regex FilePathPattern = new(
        @"\b(?:[A-Za-z]:\\|~?/)?(?:[\w.-]+[\\/])+[\w.-]+\.[A-Za-z0-9]{1,6}\b", RegexOptions.Compiled);

    // Dotted identifier paths: Netclaw.Configuration.MemoryConfig, MemoryConfig.Curation.
    private static readonly Regex DottedPathPattern = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*){1,}\b", RegexOptions.Compiled);

    // camelCase: maxOutputTokens.
    private static readonly Regex CamelCasePattern = new(
        @"\b[a-z]+[A-Z][a-zA-Z0-9]*\b", RegexOptions.Compiled);

    // snake_case: nominator_similarity_threshold.
    private static readonly Regex SnakeCasePattern = new(
        @"\b[a-zA-Z][a-zA-Z0-9]*(?:_[a-zA-Z0-9]+)+\b", RegexOptions.Compiled);

    // kebab-case: memory-core-redesign.
    private static readonly Regex KebabCasePattern = new(
        @"\b[a-zA-Z][a-zA-Z0-9]*(?:-[a-zA-Z0-9]+)+\b", RegexOptions.Compiled);

    // ALL_CAPS identifiers: NOMINATOR_K, SKU42. Minimum 3 characters total to avoid
    // flagging short conversational acronyms (OK, US, ID) as load-bearing.
    private static readonly Regex AllCapsPattern = new(
        @"\b[A-Z][A-Z0-9_]{2,}\b", RegexOptions.Compiled);

    private static readonly Regex[] TokenPatterns =
    [
        UrlPattern,
        IsoDatePattern, WrittenDatePattern, ReverseWrittenDatePattern, SlashDatePattern,
        VersionPattern, QuantityPattern,
        FilePathPattern, DottedPathPattern,
        CamelCasePattern, SnakeCasePattern, KebabCasePattern, AllCapsPattern,
        BareIntegerPattern
    ];

    /// <summary>
    /// Validates a synthesized merge body against every source body it claims to combine.
    /// </summary>
    /// <param name="sourceBodies">
    /// Every body the merge is supposed to losslessly union — for an UPDATE decision, the
    /// target document's current content plus the proposal; for CONSOLIDATE, every
    /// consolidation target's content plus the proposal.
    /// </param>
    /// <param name="mergedBody">The LLM-synthesized merged body to validate.</param>
    public static MergeGuardResult Validate(IReadOnlyList<string> sourceBodies, string mergedBody)
    {
        ArgumentNullException.ThrowIfNull(sourceBodies);
        mergedBody ??= string.Empty;

        if (sourceBodies.Count == 0)
            return new MergeGuardResult(true, [], "no source bodies to validate against");

        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longestSourceLength = 0;
        foreach (var source in sourceBodies)
        {
            if (string.IsNullOrEmpty(source))
                continue;

            longestSourceLength = Math.Max(longestSourceLength, source.Length);
            foreach (var token in ExtractLoadBearingTokens(source))
                union.Add(token);
        }

        var missing = union
            .Where(token => !mergedBody.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var retainedCount = union.Count - missing.Length;
        var retentionRatio = union.Count == 0 ? 1.0 : (double)retainedCount / union.Count;
        var retentionOk = retentionRatio >= RetentionThreshold;

        var lengthRatio = longestSourceLength == 0 ? 1.0 : (double)mergedBody.Length / longestSourceLength;
        var lengthOk = lengthRatio >= LengthCollapseThreshold;

        var passed = retentionOk && lengthOk;
        var reason = !retentionOk
            ? $"retention {retentionRatio:P0} below {RetentionThreshold:P0} floor — missing {missing.Length}/{union.Count} load-bearing tokens"
            : !lengthOk
                ? $"merged length {mergedBody.Length} is only {lengthRatio:P0} of longest source ({longestSourceLength} chars), below the {LengthCollapseThreshold:P0} collapse floor"
                : $"retained {retainedCount}/{union.Count} load-bearing tokens ({retentionRatio:P0}); merged length {mergedBody.Length} is {lengthRatio:P0} of longest source ({longestSourceLength} chars)";

        return new MergeGuardResult(passed, missing, reason);
    }

    private static IEnumerable<string> ExtractLoadBearingTokens(string text)
    {
        foreach (var pattern in TokenPatterns)
        {
            foreach (Match match in pattern.Matches(text))
            {
                var token = match.Value.TrimEnd('.', ',', ':', ';', ')', ']');
                if (token.Length > 0)
                    yield return token;
            }
        }
    }
}
