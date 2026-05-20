// -----------------------------------------------------------------------
// <copyright file="SlackTextProtectorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Slack;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackTextProtectorTests
{
    // ---- Bare URL handling -------------------------------------------------

    [Fact]
    public void SafeBareUrl_IsWrappedInAngleBrackets()
    {
        var result = SlackTextProtector.ProtectUrls("Visit https://example.com for details.");

        Assert.Equal("Visit <https://example.com> for details.", result);
    }

    [Fact]
    public void BareUrlWithLiteralPlus_RenderedAsInlineCode()
    {
        // The classic OAuth-scope bug shape: the URL emitted by the
        // MCP has literal '+' separating scopes. Slack's click
        // redirector rewrites that to '%2B'. Render as inline code so
        // the URL is non-clickable and the user copies it intact.
        var url = "https://accounts.google.com/o/oauth2/auth?scope=A+B+C&state=xyz";

        var result = SlackTextProtector.ProtectUrls("Auth: " + url);

        Assert.Equal($"Auth: `{url}`", result);
    }

    [Fact]
    public void BareUrlWithMisencodedScopeList_DecodedAndRenderedAsCode()
    {
        // The LLM-introduced corruption shape: '%2B' (URL-encoded '+')
        // separating scopes inside a 'scope=' parameter. Restore '+'
        // so the URL is what the IdP expects, then render as inline
        // code so Slack doesn't re-corrupt on click.
        var corrupted = "https://accounts.google.com/o/oauth2/auth?scope=A%2BB%2BC&state=xyz";
        var restored = "https://accounts.google.com/o/oauth2/auth?scope=A+B+C&state=xyz";

        var result = SlackTextProtector.ProtectUrls("Auth: " + corrupted);

        Assert.Equal($"Auth: `{restored}`", result);
    }

    [Fact]
    public void BareUrlWithSinglePlusEncoded_LeftAlone()
    {
        // A single '%2B' is more likely a legitimate literal '+' in
        // the URL than a scope-list separator. The decoder must not
        // touch it. The URL is still flagged as rewrite-prone (so
        // Slack doesn't make it clickable) — but we don't decode.
        var url = "https://example.com/path/file%2Bversion?id=42";

        var result = SlackTextProtector.ProtectUrls("Doc: " + url);

        Assert.Equal($"Doc: `{url}`", result);
    }

    [Fact]
    public void BareUrlWithEncodedSpace_StaysClickable()
    {
        // '%20' is a URL-encoded space — not a rewrite trigger, URL
        // remains clickable.
        var input = "Look at https://example.com/?q=foo%20bar here";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal("Look at <https://example.com/?q=foo%20bar> here", result);
    }

    [Theory]
    [InlineData("Visit https://example.com.", "Visit <https://example.com>.")]
    [InlineData("Excited https://example.com!", "Excited <https://example.com>!")]
    [InlineData("Right? https://example.com?", "Right? <https://example.com>?")]
    [InlineData("Done: https://example.com;", "Done: <https://example.com>;")]
    [InlineData("Two https://example.com, then more", "Two <https://example.com>, then more")]
    [InlineData("Trailing https://example.com...", "Trailing <https://example.com>...")]
    [InlineData("Path https://example.com/a.b.c.", "Path <https://example.com/a.b.c>.")]
    public void BareUrlAtSentenceEnd_DoesNotSwallowTrailingPunctuation(
        string input, string expected)
    {
        // The URL token must stop before sentence punctuation —
        // otherwise Slack treats e.g. the period as part of the
        // clickable link target. Punctuation inside the URL path is
        // still preserved (only the final character is constrained).
        Assert.Equal(expected, SlackTextProtector.ProtectUrls(input));
    }

    // ---- Markdown link handling --------------------------------------------

    [Fact]
    public void MarkdownLink_WithSafeUrl_ConvertedToMrkdwnLink()
    {
        var input = "Read [the docs](https://example.com/docs).";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal("Read <https://example.com/docs|the docs>.", result);
    }

    [Fact]
    public void MarkdownLink_WithMisencodedScopeList_DecodedToInlineCode()
    {
        // End-to-end bug-of-record: bot wraps URL in markdown link form
        // and URL-encodes '+' → '%2B' inside. Decode back, drop label,
        // emit URL as inline code.
        var corrupted = "https://accounts.google.com/o/oauth2/auth?scope=https%3A%2F%2Fa%2Bhttps%3A%2F%2Fb%2Bhttps%3A%2F%2Fc&state=xyz";
        var restored = "https://accounts.google.com/o/oauth2/auth?scope=https%3A%2F%2Fa+https%3A%2F%2Fb+https%3A%2F%2Fc&state=xyz";
        var input = $"Authorise [here]({corrupted}).";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal($"Authorise `{restored}`.", result);
    }

    [Fact]
    public void MarkdownLink_WithPlusInUrl_DropsLabelAndRendersAsCode()
    {
        var input = "Read [the docs](https://example.com/docs?a=b+c).";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal("Read `https://example.com/docs?a=b+c`.", result);
    }

    [Fact]
    public void MarkdownLink_UrlWithParentheses_NotTruncated()
    {
        // Regression for #1107: the markdown link destination must not
        // be cut off at the first ')'. A balanced '(...)' inside the
        // URL is part of the URL — only an unbalanced ')' closes the link.
        var input = "Read [the article](https://en.wikipedia.org/wiki/Foo_(disambiguation)) now.";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(
            "Read <https://en.wikipedia.org/wiki/Foo_(disambiguation)|the article> now.",
            result);
    }

    [Fact]
    public void MarkdownLink_UrlWithParentheses_FollowedByProseParens_SplitCorrectly()
    {
        // The ')' that closes the markdown link must be distinguished
        // from prose parentheses that follow it.
        var input = "See [doc](https://e.com/Foo_(bar)) (really).";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal("See <https://e.com/Foo_(bar)|doc> (really).", result);
    }

    [Fact]
    public void MarkdownLink_UrlWithMultipleParenGroups_NotTruncated()
    {
        var input = "[wiki](https://e.com/a_(b)_(c))";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal("<https://e.com/a_(b)_(c)|wiki>", result);
    }

    // ---- Already-protected URLs --------------------------------------------

    [Fact]
    public void UrlAlreadyInAngleBrackets_LeftUntouched()
    {
        var input = "Login via <https://accounts.google.com/auth?a=b+c>.";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void UrlAlreadyInBackticks_LeftUntouched()
    {
        var input = "Auth URL: `https://accounts.google.com/auth?a=b+c`";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void MrkdwnLinkWithLabel_LeftUntouched()
    {
        var input = "See the <https://example.com|docs> for details.";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(input, result);
    }

    // ---- Multiplicity and mixed input --------------------------------------

    [Fact]
    public void MultipleSafeBareUrls_AllWrapped()
    {
        var input = "First https://a.example.com then https://b.example.com end.";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(
            "First <https://a.example.com> then <https://b.example.com> end.",
            result);
    }

    [Fact]
    public void MixedMarkdownAndBareUrls_HandledTogether()
    {
        var input = "See [docs](https://docs.example.com) or https://example.com directly.";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(
            "See <https://docs.example.com|docs> or <https://example.com> directly.",
            result);
    }

    [Fact]
    public void TextWithoutUrls_LeftUntouched()
    {
        var input = "No URLs in this sentence at all.";

        Assert.Equal(input, SlackTextProtector.ProtectUrls(input));
    }

    [Fact]
    public void EmptyString_ReturnedAsEmpty()
    {
        Assert.Equal(string.Empty, SlackTextProtector.ProtectUrls(string.Empty));
    }

    [Fact]
    public void NullInput_ReturnedAsEmpty()
    {
        Assert.Equal(string.Empty, SlackTextProtector.ProtectUrls(null));
    }

    [Fact]
    public void HttpUrl_AlsoHandled()
    {
        var input = "Local: http://127.0.0.1:8765/oauth2callback?state=abc";

        Assert.Equal(
            "Local: <http://127.0.0.1:8765/oauth2callback?state=abc>",
            SlackTextProtector.ProtectUrls(input));
    }

    [Fact]
    public void UrlInsideProseParentheses_StillWrapped()
    {
        var input = "(see https://x.com)";

        Assert.Equal("(see <https://x.com>)", SlackTextProtector.ProtectUrls(input));
    }

    // ---- End-to-end realistic OAuth URL ------------------------------------

    [Fact]
    public void RealisticMisencodedOAuthMarkdownLink_DecodesAllScopesIntact()
    {
        var corrupted = "https://accounts.google.com/o/oauth2/auth?response_type=code"
                      + "&client_id=abc.apps.googleusercontent.com"
                      + "&redirect_uri=http%3A%2F%2Flocalhost%3A8765%2Foauth2callback"
                      + "&scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fgmail.readonly"
                      + "%2Bhttps%3A%2F%2Fwww.googleapis.com%2Fauth%2Fgmail.modify"
                      + "%2Bhttps%3A%2F%2Fwww.googleapis.com%2Fauth%2Fcalendar"
                      + "%2Bopenid"
                      + "&state=06f15c277de36e0aa18608f18f41defc";
        var restored = corrupted.Replace("%2B", "+");

        var result = SlackTextProtector.ProtectUrls($"Authorise [here]({corrupted}).");

        Assert.Equal($"Authorise `{restored}`.", result);
    }

    // ---- Direct invariants on the helpers ----------------------------------

    [Theory]
    [InlineData("https://example.com/a+b", true)]
    [InlineData("https://example.com/a%2Bb", true)]
    [InlineData("https://example.com/A%2bb", true)]
    [InlineData("https://example.com/normal", false)]
    [InlineData("", false)]
    public void IsRewriteProne_DetectsBothPlusAndPercentTwoB(string url, bool expected)
    {
        Assert.Equal(expected, SlackTextProtector.IsRewriteProne(url));
    }

    [Theory]
    // Multiple %2B in scope= → decode all of them.
    [InlineData(
        "https://x.com/auth?scope=A%2BB%2BC&state=1",
        "https://x.com/auth?scope=A+B+C&state=1")]
    // Single %2B in scope= → leave alone (likely literal '+').
    [InlineData(
        "https://x.com/auth?scope=A%2BB&state=1",
        "https://x.com/auth?scope=A%2BB&state=1")]
    // %2B outside scope= → leave alone.
    [InlineData(
        "https://x.com/path%2Bv?id=1&scope=A",
        "https://x.com/path%2Bv?id=1&scope=A")]
    // No scope= → leave alone.
    [InlineData(
        "https://x.com/?a=b%2Bc%2Bd",
        "https://x.com/?a=b%2Bc%2Bd")]
    // Scope= is the last parameter.
    [InlineData(
        "https://x.com/auth?state=1&scope=A%2BB%2BC",
        "https://x.com/auth?state=1&scope=A+B+C")]
    // Already correct → unchanged.
    [InlineData(
        "https://x.com/auth?scope=A+B+C",
        "https://x.com/auth?scope=A+B+C")]
    // 'scope=' embedded in a longer parameter name ('myscope=') is not
    // an OAuth scope list — must be left alone even with multiple %2B.
    [InlineData(
        "https://x.com/auth?myscope=A%2BB%2BC&state=1",
        "https://x.com/auth?myscope=A%2BB%2BC&state=1")]
    // A 'myscope=' decoy earlier in the query string must not shadow
    // the real boundary-anchored 'scope=' parameter.
    [InlineData(
        "https://x.com/auth?myscope=keep%2Bme&scope=A%2BB%2BC",
        "https://x.com/auth?myscope=keep%2Bme&scope=A+B+C")]
    public void NormaliseScopeList_DecodesOnlyOAuthScopePattern(string input, string expected)
    {
        Assert.Equal(expected, SlackTextProtector.NormaliseScopeList(input));
    }
}
