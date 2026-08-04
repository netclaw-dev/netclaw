// -----------------------------------------------------------------------
// <copyright file="OAuthRedirectParser.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Providers.OAuth;

/// <summary>
/// Parses OAuth redirect URLs to extract authorization code and state parameters.
/// Used for the paste-redirect-URL fallback in headless environments.
/// </summary>
public static class OAuthRedirectParser
{
    /// <summary>
    /// Try to extract <c>code</c>, <c>state</c>, and <c>iss</c> query parameters from a pasted
    /// redirect URL.
    /// </summary>
    /// <param name="input">The pasted URL string.</param>
    /// <param name="code">The extracted authorization code.</param>
    /// <param name="state">The extracted state parameter.</param>
    /// <param name="iss">
    /// The RFC 9207 issuer identifier, or null when the authorization server sent none. An
    /// authorization server that advertises iss support rejects a response without it, so this
    /// value must reach the consumer that validates it.
    /// </param>
    /// <param name="error">A user-friendly error message if parsing fails.</param>
    /// <returns>True if both code and state were successfully extracted.</returns>
    public static bool TryParse(
        string? input,
        out string code,
        out string state,
        out string? iss,
        out string? error)
    {
        code = "";
        state = "";
        iss = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "URL is empty.";
            return false;
        }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Not a valid URL. Paste the full redirect URL from your browser's address bar.";
            return false;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        var codeValue = query["code"];
        var stateValue = query["state"];

        if (string.IsNullOrEmpty(codeValue))
        {
            error = "URL is missing the 'code' parameter. Make sure you paste the complete URL after authorization.";
            return false;
        }

        if (string.IsNullOrEmpty(stateValue))
        {
            error = "URL is missing the 'state' parameter. Make sure you paste the complete URL after authorization.";
            return false;
        }

        code = codeValue;
        state = stateValue;
        iss = query["iss"];
        return true;
    }
}
