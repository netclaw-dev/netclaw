using System.Text;
using System.Text.Json;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Extracts the ChatGPT Account ID from a JWT access token.
/// The Codex backend requires this as the <c>ChatGPT-Account-Id</c> header.
/// </summary>
internal static class JwtAccountIdExtractor
{
    /// <summary>
    /// Extracts the organization/account ID from the JWT payload.
    /// Returns null if extraction fails (malformed token, missing claim).
    /// </summary>
    public static string? Extract(string accessToken)
    {
        // JWT format: header.payload.signature
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // Try "oid" claim first (OpenAI Codex standard)
            if (root.TryGetProperty("oid", out var oid) && oid.ValueKind == JsonValueKind.String)
                return oid.GetString();

            // Fallback: first entry in "orgs" array
            if (root.TryGetProperty("orgs", out var orgs) && orgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var org in orgs.EnumerateArray())
                {
                    if (org.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        return id.GetString();
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
