// -----------------------------------------------------------------------
// <copyright file="OAuthTokenExpiryStorage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;

namespace Netclaw.Configuration;

/// <summary>
/// Owner of the value rules for <c>Providers.&lt;name&gt;.OAuthTokenExpiry</c>. Netclaw keeps this
/// value in secrets.json. <see cref="SecretsFileWriter.IsPlaintextLeaf"/> keeps it unencrypted so the
/// configuration binder can read it as a date.
/// </summary>
public static class OAuthTokenExpiryStorage
{
    /// <summary>
    /// Parse the stored text with the rule of the configuration binder. Null or empty text is a
    /// valid "no expiry" value, because the binder reads an empty string as null.
    /// </summary>
    public static bool TryParse(string? text, out DateTimeOffset? expiry)
    {
        if (string.IsNullOrEmpty(text))
        {
            expiry = null;
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            expiry = parsed;
            return true;
        }

        expiry = null;
        return false;
    }

    /// <summary>
    /// Parse the stored text and fail loudly when it is not a date. <paramref name="location"/>
    /// names the file and key for the operator.
    /// </summary>
    public static DateTimeOffset? Parse(string? text, string location)
    {
        if (TryParse(text, out var expiry))
            return expiry;

        throw new InvalidDataException($"{location} is not a date: '{text}'.");
    }
}
