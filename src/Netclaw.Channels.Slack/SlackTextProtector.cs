// -----------------------------------------------------------------------
// <copyright file="SlackTextProtector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Defensive transforms applied to the plain-text fallback of an outbound
/// Slack message before it's posted via <c>chat.postMessage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Slack auto-linkifies bare URLs in the <c>Text</c> field and routes
/// every click through its own link redirector. Both stages can rewrite
/// reserved characters in the URL — most importantly the <c>+</c>
/// (URL-encoded space) used by OAuth scope lists is re-encoded to
/// <c>%2B</c> on click. Google then sees every scope concatenated into
/// one invalid scope string and rejects the auth flow with
/// <c>Error 400: invalid_scope</c>.
/// </para>
/// <para>
/// Wrapping the URL in Slack mrkdwn angle brackets
/// (<c>&lt;https://example.com&gt;</c>) helps with copy-paste from the
/// rendered message but does <em>not</em> stop the click-time rewrite
/// because Slack still routes through its link service. The only
/// reliable way to preserve such a URL is to make it non-clickable —
/// rendered as inline code (single backticks). Slack does not
/// auto-linkify text inside backticks, so there's no click and no
/// rewrite.
/// </para>
/// <para>
/// This helper applies two transforms to the <c>Text</c> field:
/// <list type="number">
///   <item>Convert standard markdown links <c>[text](url)</c> into the
///   equivalent Slack mrkdwn form <c>&lt;url|text&gt;</c>.</item>
///   <item>Process bare URLs. URLs that contain a <c>+</c> (the
///   documented OAuth-scope bug) are wrapped in backticks for safe
///   manual copy. All other URLs are wrapped in mrkdwn angle brackets
///   so they render as proper clickable links.</item>
/// </list>
/// URLs already inside <c>&lt;...&gt;</c> or backticks are left alone.
/// </para>
/// <para>
/// The block-kit (<c>Blocks</c>) representation of the same message is
/// handled separately by <see cref="SlackBlockConverter"/>, which
/// applies the same is-it-safe-to-link heuristic when emitting
/// <c>RichTextLink</c> vs. <c>RichTextText</c> with code style.
/// </para>
/// </remarks>
public static partial class SlackTextProtector
{
    /// <summary>
    /// Returns <paramref name="text"/> with markdown links converted to
    /// Slack mrkdwn and bare URLs wrapped in either angle brackets
    /// (safe, clickable) or backticks (rewrite-prone, non-clickable
    /// for copy-paste). URLs already protected are left alone.
    /// </summary>
    public static string ProtectUrls(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        // 1. Convert markdown [text](url) into the right Slack form.
        //    Safe URL → Slack mrkdwn link <url|label>. Rewrite-prone
        //    URL → inline code with the URL as the visible payload
        //    (label is dropped because the URL itself must be visible
        //    for the user to copy). Either way the surrounding '(' / ')'
        //    are consumed so the bare-URL pass below doesn't have to
        //    skip them.
        var afterMarkdown = MarkdownLinkRegex().Replace(text, m =>
        {
            var label = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            return IsRewriteProne(url)
                ? $"`{url}`"
                : $"<{url}|{label}>";
        });

        // 2. Wrap bare URLs that aren't already inside angle brackets
        //    or backticks. The MatchEvaluator inspects the surrounding
        //    characters in the snapshot to decide how to wrap.
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

            // The URL sits at a pipe boundary inside an existing
            // mrkdwn link (e.g. "<https://a|label>"). The
            // start-with-'<' check above already covers that, but
            // also leave alone if we see the pipe form on either side.
            if (end < snapshot.Length && snapshot[end] == '|')
                return match.Value;

            // URLs containing '+' will be rewritten by Slack's link
            // redirector at click time. Render as inline code so the
            // URL is non-clickable and the user copies it as-is.
            return IsRewriteProne(match.Value)
                ? $"`{match.Value}`"
                : $"<{match.Value}>";
        });
    }

    /// <summary>
    /// Returns <c>true</c> if Slack's link redirector is known to
    /// rewrite this URL on click in a way that loses information.
    /// </summary>
    /// <remarks>
    /// Today the only documented case is <c>+</c> (URL-encoded space)
    /// being re-encoded to <c>%2B</c>, which breaks OAuth scope lists.
    /// Extend this predicate as further patterns are confirmed.
    /// </remarks>
    public static bool IsRewriteProne(string url)
        => !string.IsNullOrEmpty(url) && url.Contains('+', StringComparison.Ordinal);

    // Markdown link: [label](url). Captures label + url separately.
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex MarkdownLinkRegex();

    // Bare http/https URL. Stops at whitespace and at the characters
    // that close a wrapping mrkdwn link or markdown construct.
    [GeneratedRegex(@"https?://[^\s<>)\]|]+")]
    private static partial Regex BareUrlRegex();
}
