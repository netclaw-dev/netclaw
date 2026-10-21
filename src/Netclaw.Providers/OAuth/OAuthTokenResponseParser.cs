// -----------------------------------------------------------------------
// <copyright file="OAuthTokenResponseParser.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.OAuth;

internal static class OAuthTokenResponseParser
{
    // RFC 6749 puts a string code in "error". auth.openai.com puts an object there instead:
    // {"error":{"code":"refresh_token_reused",...}}. These codes mean the refresh token is spent or
    // invalid, so only a new sign-in helps and a retry cannot.
    private static readonly HashSet<string> RejectedRefreshTokenCodes = new(StringComparer.Ordinal)
    {
        "invalid_grant",
        "refresh_token_reused",
        "refresh_token_expired",
        "refresh_token_invalidated",
    };

    /// <summary>
    /// Turns a failed refresh response into an exception. A rejected refresh token throws
    /// <see cref="OAuthRefreshTokenRejectedException"/>. Every other failure throws an
    /// <see cref="HttpRequestException"/> that carries the status, so the caller can tell a
    /// transient failure (5xx, 429) from a permanent one.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowRefreshFailure(HttpStatusCode status, string body)
    {
        // A 5xx or 429 body, often HTML from a proxy, says nothing about the refresh token.
        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"The token server answered HTTP {(int)status}.", null, status);

        string? code = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                code = error.ValueKind switch
                {
                    JsonValueKind.String => error.GetString(),
                    JsonValueKind.Object when error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String => c.GetString(),
                    _ => null,
                };
            }
        }
        catch (JsonException)
        {
            // Handled below: a body that is not JSON has no code.
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new HttpRequestException(
                $"The token server answered HTTP {(int)status} with a body that holds no OAuth error code. "
                + "The body is empty, is not JSON, or has no \"error\" string or \"error.code\" string.",
                null,
                status);
        }

        if (RejectedRefreshTokenCodes.Contains(code))
            throw new OAuthRefreshTokenRejectedException(code);

        throw new HttpRequestException($"The token server answered HTTP {(int)status} with the error '{code}'.", null, status);
    }

    public static OAuthDeviceFlowResult Parse(JsonElement root, TimeProvider timeProvider)
    {
        var accessToken = GetRequiredString(root, "access_token");
        var refreshToken = GetOptionalString(root, "refresh_token");
        DateTimeOffset? expiresAt = ReadOptionalSeconds(root, "expires_in") is { } seconds
            ? AddSecondsClamped(timeProvider.GetUtcNow(), seconds)
            : null;
        var accountId = ExtractAccountId(root);

        return new OAuthDeviceFlowResult(
            new SensitiveString(accessToken),
            refreshToken is not null ? new SensitiveString(refreshToken) : null,
            expiresAt,
            accountId is not null ? new SensitiveString(accountId) : null);
    }

    public static string? ExtractChatGptAccountId(string jwt)
    {
        if (!TryDecodeJwtPayload(jwt, out var payload))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (TryGetChatGptAccountId(root, out var accountId))
                return accountId;
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (TryGetString(root, propertyName, out var value))
            return value;

        throw new InvalidOperationException($"Missing {propertyName} in OAuth token response.");
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
        => TryGetString(root, propertyName, out var value) ? value : null;

    private static string? ExtractAccountId(JsonElement root)
    {
        if (TryGetAccountIdValue(root, "account_id", out var accountId))
            return accountId;

        if (TryGetChatGptAccountId(root, out var chatGptAccountId))
            return chatGptAccountId;

        var idToken = GetOptionalString(root, "id_token");
        return idToken is null ? null : ExtractChatGptAccountId(idToken);
    }

    private static bool TryGetChatGptAccountId(JsonElement root, out string accountId)
    {
        if (TryGetAccountIdValue(root, "chatgpt_account_id", out accountId))
            return true;

        if (root.TryGetProperty("https://api.openai.com/auth", out var auth)
            && auth.ValueKind == JsonValueKind.Object
            && TryGetAccountIdValue(auth, "chatgpt_account_id", out accountId))
        {
            return true;
        }

        accountId = "";
        return false;
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        value = candidate;
        return true;
    }

    private static bool TryGetAccountIdValue(JsonElement root, string propertyName, out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.String)
        {
            var candidate = property.GetString();
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            value = candidate;
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numericValue))
        {
            value = numericValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    // A token endpoint can return an absurd or non-finite expires_in; DateTimeOffset
    // .AddSeconds throws ArgumentOutOfRangeException on overflow and on NaN. Clamp to
    // the representable range so a bad value degrades to "effectively never expires"
    // instead of aborting the whole auth/refresh flow with a non-actionable error.
    private static DateTimeOffset AddSecondsClamped(DateTimeOffset now, double seconds)
    {
        if (double.IsNaN(seconds))
            return now;

        if (seconds >= (DateTimeOffset.MaxValue - now).TotalSeconds)
            return DateTimeOffset.MaxValue;

        if (seconds <= (DateTimeOffset.MinValue - now).TotalSeconds)
            return DateTimeOffset.MinValue;

        return now.AddSeconds(seconds);
    }

    private static double? ReadOptionalSeconds(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
            return null;

        // An explicit JSON null is treated the same as an absent property (no expiry)
        // rather than a hard parse failure.
        if (property.ValueKind == JsonValueKind.Null)
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numericValue))
            return numericValue;

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringValue))
        {
            return stringValue;
        }

        throw new InvalidOperationException($"Invalid {propertyName} in OAuth token response.");
    }

    private static bool TryDecodeJwtPayload(string jwt, out byte[] payload)
    {
        payload = [];

        var parts = jwt.Split('.');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            return false;

        try
        {
            payload = Base64UrlDecode(parts[1]);
            return true;
        }
        catch (FormatException)
        {
            return false;
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
