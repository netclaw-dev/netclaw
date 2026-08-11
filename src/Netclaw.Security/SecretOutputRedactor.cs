// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Security;

/// <summary>
/// Redacts common secret-bearing patterns from tool output before returning text to the LLM.
/// This is defense-in-depth for accidental leakage; not a replacement for access controls.
/// </summary>
public static partial class SecretOutputRedactor
{
    private const int MaxSecretKeyChars = 512;
    private const string Redacted = "***REDACTED***";

    private static readonly string[] SecretKeyFragments =
    [
        "apikey",
        "token",
        "secret",
        "password",
        "authorization",
        "credential",
        "privatekey",
        "signingkey",
        "connectionstring"
    ];

    internal static bool IsSecretKey(string key)
    {
        // A hostile MCP schema can supply arbitrarily large property names.
        // Fail closed instead of allocating an equally large normalized key
        // just to decide whether its value is safe to display.
        if (key.Length > MaxSecretKeyChars)
            return true;

        var normalized = Netclaw.Tools.ToolArgumentHelper.NormalizeKey(key);
        return SecretKeyFragments.Any(fragment =>
            normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ContainsSecretLikeContent(string output)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        return !string.Equals(Redact(output), output, StringComparison.Ordinal);
    }

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

        sanitized = ProviderTokenRegex().Replace(sanitized, Redacted);

        sanitized = AwsAccessKeyRegex().Replace(sanitized, Redacted);

        sanitized = JwtTokenRegex().Replace(sanitized, Redacted);

        sanitized = PrivateKeyBlockRegex().Replace(sanitized, Redacted);

        return sanitized;
    }

    [GeneratedRegex("\"((?:api[_-]?key|token|secret|password|authorization|access[_-]?token|refresh[_-]?token|client[_-]?secret|signing[_-]?key|private[_-]?key|connection[_-]?string|credential)[^\"]*)\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretValueRegex();

    [GeneratedRegex("\\b((?:api[_-]?key|token|secret|password|authorization|credential)[A-Z0-9_-]*)=([^\\s;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvSecretValueRegex();

    [GeneratedRegex("(Authorization\\s*:\\s*Bearer\\s+)(\\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderSecretValueRegex();

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
