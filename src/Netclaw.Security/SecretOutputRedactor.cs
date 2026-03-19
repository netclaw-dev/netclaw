using System.Text.RegularExpressions;

namespace Netclaw.Security;

/// <summary>
/// Redacts common secret-bearing patterns from tool output before returning text to the LLM.
/// This is defense-in-depth for accidental leakage; not a replacement for access controls.
/// </summary>
public static partial class SecretOutputRedactor
{
    private const string Redacted = "***REDACTED***";

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

        sanitized = EnvSecretValueRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}={Redacted}");

        sanitized = HeaderSecretValueRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted}");

        sanitized = ProviderTokenRegex().Replace(sanitized, Redacted);

        sanitized = PrivateKeyBlockRegex().Replace(sanitized, Redacted);

        return sanitized;
    }

    [GeneratedRegex("\"((?:api[_-]?key|token|secret|password|authorization|access[_-]?token|refresh[_-]?token)[^\"]*)\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretValueRegex();

    [GeneratedRegex("\\b((?:api[_-]?key|token|secret|password|authorization)[A-Z0-9_-]*)=([^\\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvSecretValueRegex();

    [GeneratedRegex("(Authorization\\s*:\\s*Bearer\\s+)(\\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderSecretValueRegex();

    [GeneratedRegex("\\b(sk-[A-Za-z0-9_-]{8,}|xox[baprs]-[A-Za-z0-9-]{8,}|ghp_[A-Za-z0-9]{20,})\\b")]
    private static partial Regex ProviderTokenRegex();

    [GeneratedRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]+?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlockRegex();
}
