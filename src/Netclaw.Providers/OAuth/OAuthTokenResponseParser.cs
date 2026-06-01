// -----------------------------------------------------------------------
// <copyright file="OAuthTokenResponseParser.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.OAuth;

internal static class OAuthTokenResponseParser
{
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
