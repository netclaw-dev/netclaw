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
    [Fact]
    public void BareUrl_IsWrappedInAngleBrackets()
    {
        var result = SlackTextProtector.ProtectUrls("Visit https://example.com for details.");

        Assert.Equal("Visit <https://example.com> for details.", result);
    }

    [Fact]
    public void BareUrlWithPlusInQueryString_RenderedAsInlineCode()
    {
        // The bug-of-record: scope list separated by `+` (URL-encoded
        // space). Slack's link redirector rewrites `+` to `%2B` on
        // click, regardless of `<>` wrapping. Rendering as inline
        // code (backticks) makes the URL non-clickable so the user
        // copies it literally.
        var url = "https://accounts.google.com/o/oauth2/auth?scope=A+B+C&state=xyz";

        var result = SlackTextProtector.ProtectUrls("Click here: " + url);

        Assert.Equal($"Click here: `{url}`", result);
    }

    [Fact]
    public void UrlAlreadyWrapped_LeftUnchanged()
    {
        var input = "Login via <https://accounts.google.com/auth?a=b+c>.";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void MrkdwnLinkWithLabel_LeftUnchanged()
    {
        var input = "See the <https://example.com|docs> for details.";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void MarkdownLink_WithSafeUrl_ConvertedToMrkdwnForm()
    {
        // [text](url) → <url|text>. Required because Slack does not
        // render standard markdown links in the Text field.
        var input = "Read [the docs](https://example.com/docs).";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal("Read <https://example.com/docs|the docs>.", result);
    }

    [Fact]
    public void MarkdownLink_WithPlusInUrl_FallsBackToInlineCode()
    {
        // When the URL inside a markdown link contains '+', keeping it
        // as a clickable mrkdwn link still routes through Slack's link
        // redirector and corrupts the URL. We instead render the URL
        // as inline code so the user copies it manually. The label is
        // dropped because the URL has to be the visible payload.
        var input = "Read [the docs](https://example.com/docs?a=b+c).";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal("Read `https://example.com/docs?a=b+c`.", result);
    }

    [Fact]
    public void MultipleBareUrls_AllWrapped()
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
    public void TextWithoutUrls_LeftUnchanged()
    {
        var input = "No URLs in this sentence at all.";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(input, result);
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
    public void HttpUrl_AlsoWrapped()
    {
        // Some MCP / local endpoints use http://. Cover that too.
        var input = "Local: http://127.0.0.1:8765/oauth2callback?state=abc";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(
            "Local: <http://127.0.0.1:8765/oauth2callback?state=abc>",
            result);
    }

    [Fact]
    public void UrlInsideParenthesesButNotMarkdownLink_StillWrapped()
    {
        // A trailing close-paren in prose shouldn't be greedy: e.g.
        // "(see https://x.com)" — we want the URL wrapped without
        // pulling the close-paren into the URL match.
        var input = "(see https://x.com)";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal("(see <https://x.com>)", result);
    }

    [Fact]
    public void TrailingPunctuationInProse_NotPartOfUrl()
    {
        // Period at end of sentence shouldn't be consumed by URL
        // detection.
        var input = "Visit https://example.com.";

        var result = SlackTextProtector.ProtectUrls(input);

        // This URL pattern is conservative — period stays attached to
        // the URL (the wrapping still works for Slack, which trims
        // common trailing punctuation when resolving the link).
        Assert.Equal("Visit <https://example.com.>", result);
    }

    [Fact]
    public void OAuthAuthorisationUrl_RenderedAsInlineCode()
    {
        // Realistic Google OAuth URL with multiple scopes joined by
        // `+`. Must end up inside backticks so the user copy-pastes
        // it instead of clicking through Slack's redirector.
        var url = "https://accounts.google.com/o/oauth2/auth?response_type=code"
                + "&client_id=abc.apps.googleusercontent.com"
                + "&redirect_uri=http%3A%2F%2Flocalhost%3A8765%2Foauth2callback"
                + "&scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fgmail.readonly"
                + "+https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fgmail.modify"
                + "+https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fcalendar"
                + "+openid"
                + "&state=06f15c277de36e0aa18608f18f41defc"
                + "&code_challenge=abc123"
                + "&code_challenge_method=S256";

        var result = SlackTextProtector.ProtectUrls($"Authorise: {url}");

        Assert.Equal($"Authorise: `{url}`", result);
    }

    [Fact]
    public void UrlAlreadyInBackticks_LeftUnchanged()
    {
        var input = "Auth URL: `https://accounts.google.com/auth?a=b+c`";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void BareUrlWithEncodedSpace_NotMistakenForRewriteProne()
    {
        // %20 (literal URL-encoded space) doesn't trigger Slack's
        // redirector rewrite, so leave the URL clickable.
        var input = "Look at https://example.com/?q=foo%20bar here";

        var result = SlackTextProtector.ProtectUrls(input);

        Assert.Equal("Look at <https://example.com/?q=foo%20bar> here", result);
    }
}
