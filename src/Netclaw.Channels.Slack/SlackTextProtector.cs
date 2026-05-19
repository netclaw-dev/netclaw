// -----------------------------------------------------------------------
// <copyright file="SlackTextProtector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Defensive URL transforms applied to the plain-text fallback of an
/// outbound Slack message before it's posted via <c>chat.postMessage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two distinct breakages can corrupt OAuth-shaped URLs en route from
/// an MCP tool result to a Slack DM:
/// </para>
/// <list type="number">
///   <item>The LLM rewrites the URL when constructing a markdown link
///   (<c>[label](url)</c>) — it URL-encodes the literal <c>+</c>
///   characters that delimit scope-list entries into <c>%2B</c>. The
///   URL is corrupted before NetClaw ever sees it.</item>
///   <item>Slack's auto-linkifier and link redirector transform bare
///   URLs in the <c>Text</c> field on render and on click. The
///   <c>+</c> → <c>%2B</c> rewrite happens at click time regardless of
///   how the URL was wrapped.</item>
/// </list>
/// <para>
/// The fix applied here has three pieces:
/// </para>
/// <list type="bullet">
///   <item><see cref="NormaliseScopeList"/> — when a URL has a
///   <c>scope=</c> query parameter whose value contains multiple
///   <c>%2B</c> separators, decode them back to <c>+</c>. Conservative
///   enough to leave URLs with a single legitimate <c>%2B</c>
///   untouched.</item>
///   <item><see cref="IsRewriteProne"/> — flag URLs that contain
///   <c>+</c> or <c>%2B</c> as rewrite-prone. Rewrite-prone URLs are
///   rendered as inline code so Slack never makes them clickable and
///   the user copies the exact bytes the agent produced.</item>
///   <item>Markdown links <c>[label](url)</c> are routed through the
///   same normalisation. Safe URLs become Slack mrkdwn
///   <c>&lt;url|label&gt;</c>; rewrite-prone URLs lose the label and
///   become inline-code (the URL is what the user has to be able to
///   copy).</item>
/// </list>
/// <para>
/// The Block Kit (<c>Blocks</c>) representation of the same message is
/// handled by <see cref="SlackBlockConverter"/> using the same
/// <see cref="IsRewriteProne"/> and <see cref="NormaliseScopeList"/>
/// helpers so the two surfaces stay in lockstep.
/// </para>
/// </remarks>
public static partial class SlackTextProtector
{
    /// <summary>
    /// Returns <paramref name="text"/> with markdown links normalised
    /// and bare URLs wrapped to survive Slack's auto-linkifier.
    /// </summary>
    public static string ProtectUrls(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        // 1. Process markdown [label](url) links first.
        //    Decoding the URL up-front recovers from LLM-introduced
        //    '+' → '%2B' rewrite before any further routing.
        var afterMarkdown = MarkdownLinkRegex().Replace(text, m =>
        {
            var label = m.Groups[1].Value;
            var url = NormaliseScopeList(m.Groups[2].Value);
            return IsRewriteProne(url)
                ? $"`{url}`"
                : $"<{url}|{label}>";
        });

        // 2. Wrap bare URLs that aren't already inside <...> or `...`.
        var snapshot = afterMarkdown;
        return BareUrlRegex().Replace(snapshot, match =>
        {
            var start = match.Index;
            var end = match.Index + match.Length;

            // Already inside <...> — leave untouched.
            if (start > 0 && snapshot[start - 1] == '<')
                return match.Value;
            if (end < snapshot.Length && snapshot[end] == '>')
                return match.Value;

            // Already inside `...` — leave untouched.
            if (start > 0 && snapshot[start - 1] == '`')
                return match.Value;
            if (end < snapshot.Length && snapshot[end] == '`')
                return match.Value;

            // Pipe boundary (mrkdwn link form already in flight).
            if (end < snapshot.Length && snapshot[end] == '|')
                return match.Value;

            var url = NormaliseScopeList(match.Value);
            return IsRewriteProne(url)
                ? $"`{url}`"
                : $"<{url}>";
        });
    }

    /// <summary>
    /// Returns <c>true</c> if Slack will rewrite reserved characters in
    /// this URL on render or on click.
    /// </summary>
    /// <remarks>
    /// Both literal <c>+</c> (URL-encoded space, used in OAuth scope
    /// lists) and the LLM-introduced <c>%2B</c> variant are treated as
    /// rewrite-prone — the resulting rendering must be non-clickable
    /// so the user copies the URL exactly.
    /// </remarks>
    public static bool IsRewriteProne(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;
        return url.Contains('+', StringComparison.Ordinal)
            || url.Contains("%2B", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// If <paramref name="url"/> looks like an OAuth authorisation URL
    /// whose <c>scope=</c> value has been mis-encoded with <c>%2B</c>
    /// in place of the literal <c>+</c> scope delimiter, restore the
    /// original encoding. Returns the URL unchanged in any other case.
    /// </summary>
    /// <remarks>
    /// The heuristic targets the LLM-introduced corruption pattern: a
    /// URL with a <c>scope=</c> parameter whose value contains two or
    /// more <c>%2B</c> separators. Two or more is the signal — a single
    /// <c>%2B</c> in a scope value is more likely a legitimate literal
    /// plus and is left alone.
    /// </remarks>
    public static string NormaliseScopeList(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        // Locate a 'scope=' query parameter at a parameter boundary
        // (preceded by '?' or '&'). A bare IndexOf would also match a
        // 'scope=' substring embedded in a path segment or inside a
        // longer parameter name (e.g. 'myscope='), mis-targeting the
        // decode onto an unrelated value.
        const string scopeMarker = "scope=";
        var scopeIndex = -1;
        var searchFrom = 0;
        while (true)
        {
            var hit = url.IndexOf(scopeMarker, searchFrom, StringComparison.Ordinal);
            if (hit < 0)
                break;
            if (hit > 0 && url[hit - 1] is '?' or '&')
            {
                scopeIndex = hit;
                break;
            }
            searchFrom = hit + scopeMarker.Length;
        }

        if (scopeIndex < 0)
            return url;

        var scopeStart = scopeIndex + scopeMarker.Length;
        var scopeEnd = url.IndexOf('&', scopeStart);
        if (scopeEnd < 0)
            scopeEnd = url.Length;

        var scopeValue = url[scopeStart..scopeEnd];

        // Count case-insensitive %2B occurrences in the scope value.
        var count = 0;
        for (var i = 0; i + 3 <= scopeValue.Length; i++)
        {
            if (scopeValue[i] != '%') continue;
            if (scopeValue[i + 1] != '2') continue;
            var c = scopeValue[i + 2];
            if (c == 'B' || c == 'b')
                count++;
        }

        if (count < 2)
            return url;

        var restored = scopeValue.Replace("%2B", "+", StringComparison.OrdinalIgnoreCase);
        return string.Concat(url.AsSpan(0, scopeStart), restored, url.AsSpan(scopeEnd));
    }

    // Markdown link: [label](url). Captures label (group 1) + url
    // (group 2) separately. The url group accepts a parenthesised
    // segment — e.g. "(disambiguation)" in a Wikipedia-style URL — so
    // the destination is not truncated at the first ')'. A ')' only
    // ends the link when it is not part of a balanced pair, matching
    // CommonMark link-destination semantics. One level of paren
    // nesting is supported, which covers every URL shape seen in
    // practice; deeper nesting falls back to first-')' termination.
    // Shared with SlackBlockConverter so both outbound surfaces
    // tokenize markdown links identically.
    [GeneratedRegex(@"\[([^\]]+)\]\(((?:[^()]|\([^()]*\))*)\)")]
    internal static partial Regex MarkdownLinkRegex();

    // Bare http/https URL. Stops at whitespace and at the characters
    // that close a wrapping mrkdwn link or markdown construct. The
    // required trailing character additionally excludes sentence
    // punctuation (.,!?;:) so a URL at the end of a sentence —
    // "see https://x.com." — does not swallow the period into the
    // clickable link target. Mid-URL punctuation is preserved because
    // only the final character is constrained. Shared with
    // SlackBlockConverter so both outbound surfaces tokenize URLs
    // identically.
    [GeneratedRegex(@"https?://[^\s<>)\]|]*[^\s<>)\]|.,!?;:]")]
    internal static partial Regex BareUrlRegex();
}
