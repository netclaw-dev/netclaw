// -----------------------------------------------------------------------
// <copyright file="Pre2401SecretOutputRedactor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Security.Tests;

/// <summary>
/// Verbatim copy of the redactor as it shipped in 0.27.1 (upstream/dev before PR 2401). It is the
/// reference the regression test compares the current redactor against; do not edit its patterns.
/// </summary>
internal static partial class Pre2401SecretOutputRedactor
{
    private const string Redacted = "***REDACTED***";

    public static string Redact(string output)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        var sanitized = output;

        sanitized = JsonSecretValueRegex().Replace(sanitized, m =>
            $"\"{m.Groups[1].Value}\": \"{Redacted}\"");

        sanitized = ConnectionStringPasswordRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted};");

        sanitized = EnvSecretValueRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}={Redacted}");

        sanitized = HeaderSecretValueRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted}");

        sanitized = SlackWebhookUrlRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted}");

        sanitized = ProviderTokenRegex().Replace(sanitized, Redacted);

        sanitized = AwsAccessKeyRegex().Replace(sanitized, Redacted);

        sanitized = JwtTokenRegex().Replace(sanitized, Redacted);

        sanitized = PrivateKeyBlockRegex().Replace(sanitized, Redacted);

        return sanitized;
    }

    [GeneratedRegex("\"((?:api[_-]?key|token|secret|password|authorization|access[_-]?token|refresh[_-]?token|client[_-]?secret|signing[_-]?key|private[_-]?key|connection[_-]?string|credential)[^\"]*)\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretValueRegex();

    // Kept in lockstep with JsonSecretValueRegex's key fragments: an OAuth token endpoint's
    // error body is as likely to arrive form-urlencoded ("client_secret=...&grant_type=...")
    // as JSON, and a compound key like "client_secret" or "refresh_token" does not match a
    // bare "secret"/"token" fragment here, since \b only anchors at the start of the whole
    // key -- "client_secret" has no word boundary before "secret".
    [GeneratedRegex("\\b((?:api[_-]?key|token|secret|password|authorization|access[_-]?token|refresh[_-]?token|client[_-]?secret|signing[_-]?key|private[_-]?key|connection[_-]?string|credential)[A-Z0-9_-]*)=([^\\s;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvSecretValueRegex();

    [GeneratedRegex("(Authorization\\s*:\\s*Bearer\\s+)(\\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderSecretValueRegex();

    [GeneratedRegex("(https://hooks\\.slack\\.com/services/)[A-Z0-9_-]+/[A-Z0-9_-]+/[A-Z0-9_-]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlackWebhookUrlRegex();

    [GeneratedRegex("((?:Password|Pwd)\\s*=\\s*)[^;]+;", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPasswordRegex();

    [GeneratedRegex("\\b(sk-[A-Za-z0-9_-]{8,}|xox[baprs]-[A-Za-z0-9-]{8,}|ghp_[A-Za-z0-9]{20,})\\b")]
    private static partial Regex ProviderTokenRegex();

    [GeneratedRegex("\\bAKIA[A-Z0-9]{16}\\b")]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{10,}\\.eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\b")]
    private static partial Regex JwtTokenRegex();

    [GeneratedRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]+?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlockRegex();
}
