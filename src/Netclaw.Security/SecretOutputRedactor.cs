// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Security;

/// <summary>
/// Redacts common secret-bearing patterns before text reaches an output or log boundary.
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

    public static bool IsSecretKey(string key)
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

        var beforePrefixed = sanitized;
        sanitized = PrefixedEnvSecretValueRegex().Replace(beforePrefixed, m => RedactPrefixedValue(beforePrefixed, m));

        sanitized = HeaderSecretValueRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted}");

        sanitized = SlackWebhookUrlRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted}");

        sanitized = KnownTokenRegex().Replace(sanitized, Redacted);

        sanitized = JwtTokenRegex().Replace(sanitized, Redacted);

        sanitized = PrivateKeyBlockRegex().Replace(sanitized, Redacted);

        return sanitized;
    }

    /// <summary>
    /// An exception surfaced from an external call (an OAuth token/DCR exchange, a webhook
    /// delivery, a subprocess) can carry the far side's raw error body inside
    /// <see cref="Exception.Message"/> or an inner exception, and that body can echo back a
    /// secret the request sent. Passing such an exception straight to a logger lets the
    /// unredacted body reach any log sink, including ones that leave the box (OTLP export).
    /// This swaps in a redacted stand-in only when secret-shaped content is actually
    /// present, so the overwhelming majority of exceptions (network errors, cancellations,
    /// disposal failures) keep their original instance, type, and full native stack trace.
    /// </summary>
    public static Exception RedactForLogging(Exception ex)
    {
        var rendered = ex.ToString();
        var redacted = Redact(rendered);
        if (string.Equals(redacted, rendered, StringComparison.Ordinal))
            return ex;

        var typeName = ex.GetType().FullName ?? ex.GetType().Name;
        return new RedactedLoggingException(
            $"{typeName} (message redacted; matched secret-shaped content): {redacted}");
    }

    private sealed class RedactedLoggingException(string message) : Exception(message);

    // The secret-bearing words shared by the JSON and env-style name rules.
    private const string SecretNameWords = "api[_-]?key|token|secret|password|passwd|authorization|access[_-]?token|refresh[_-]?token|client[_-]?secret|signing[_-]?key|private[_-]?key|connection[_-]?string|credential";

    // The whole whitespace-delimited value is masked, so a password that contains ')' or ','
    // is not left half visible. Only a trailing run of closers is given back, which keeps
    // "foo(auth_token=abc)" and "[db_password=abc, x]" readable. Code that merely mentions a
    // secret-named variable is left alone: "foo(auth_token=auth_token)" forwards a parameter
    // under its own name and "is_token_valid=false" is a flag. A secret is never the literal
    // true/false/null/none, nor its own name.
    private static string RedactPrefixedValue(string text, Match m)
    {
        var value = m.Groups[2].Value;
        var core = value.AsSpan().TrimEnd(")],}");
        if (core.IsEmpty || IsCodeIdiom(text, m, core))
            return m.Value;

        return $"{m.Groups[1].Value}={Redacted}{value[core.Length..]}";
    }

    private static bool IsCodeIdiom(string text, Match m, ReadOnlySpan<char> value)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("null", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var nameStart = m.Index;
        while (nameStart > 0 && (char.IsAsciiLetterOrDigit(text[nameStart - 1]) || text[nameStart - 1] == '_'))
            nameStart--;

        var name = text.AsSpan(nameStart, m.Groups[1].Index + m.Groups[1].Length - nameStart);
        return name.Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("\"((?:" + SecretNameWords + ")[^\"]*)\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretValueRegex();

    // Kept in lockstep with JsonSecretValueRegex's key words: an OAuth token endpoint's
    // error body is as likely to arrive form-urlencoded ("client_secret=...&grant_type=...")
    // as JSON. The secret word must sit at the start of a name (\b), e.g. TOKEN_FILE, --password.
    [GeneratedRegex("\\b((?:" + SecretNameWords + ")[A-Z0-9_-]*)=([^\\s;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvSecretValueRegex();

    // The same words after an underscore: DB_PASSWORD, AWS_SECRET_ACCESS_KEY, GITHUB_TOKEN.
    // Narrower than the rule above because ordinary settings carry these words too:
    // the word must be a whole segment (so not max_tokens, TOKENIZERS_*), must not follow
    // next/page/continuation/cursor (pagination cursors), the name must not end in a
    // descriptor (TOKEN_FILE, SECRET_ARN, PASSWORD_MIN_LENGTH, PASSWORD_STDIN, PASSWORD_ATTEMPTS ...), and a value that is a
    // variable or template reference (${{ secrets.X }}, ${X}, $X, $(cmd), %X%, optionally quoted) or an
    // empty quoted value is not a secret.
    // Only the secret word onwards is matched, so the prefix is left untouched. The tail is
    // bounded so a long run of "token_token_..." stays linear.
    [GeneratedRegex("(?<=_)(?<!(?:next|page|continuation|cursor)_)((?:" + SecretNameWords + ")(?![A-Z])(?>[A-Z0-9_-]{0,64}))"
        + "(?<!_(?:file|path|dir|name|arn|url|header|env|permissions|length|lifetime|endpoint|stdin|attempts|count))"
        + "=(?![\"']?(?:\\$[{(]|\\$[A-Z_]|%[A-Z_][A-Z0-9_]*%)|[\"']{2})([^\\s;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedEnvSecretValueRegex();

    [GeneratedRegex("(Authorization\\s*:\\s*Bearer\\s+)(\\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderSecretValueRegex();

    [GeneratedRegex("(https://hooks\\.slack\\.com/services/)[A-Z0-9_-]+/[A-Z0-9_-]+/[A-Z0-9_-]+", RegexOptions.IgnoreCase)]
    private static partial Regex SlackWebhookUrlRegex();

    [GeneratedRegex("((?:Password|Pwd)\\s*=\\s*)[^;]+;", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPasswordRegex();

    // Credentials recognisable by a fixed prefix and structure, redacted wherever they
    // appear, whatever the surrounding name. One alternation = one pass over the text.
    // Every entry needs a documented format and a matching test in SecretOutputRedactorTests;
    // do not add entropy or "long random string" rules: they corrupt commit SHAs, UUIDs,
    // base64 content and ids that the model must echo back exactly.
    //
    // Each entry ends its own boundary, chosen from its documented character class:
    //  - class is letters and digits only: \b, as the released redactor did, so a token that
    //    is followed by '-' or '.' ("ghp_...-backup") is still redacted;
    //  - class contains '-' and/or '_': \b cannot end the token (it may end in either), so the
    //    entry ends where its own class stops: (?![class]).
    [GeneratedRegex("\\b(?:" +
        // OpenAI (sk-, sk-proj-, sk-svcacct-) and Anthropic (sk-ant-): https://platform.openai.com/docs/api-reference/authentication , https://docs.anthropic.com/en/api/getting-started
        "sk-[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])" +
        // Slack bot/user/app/config tokens (letters, digits, '-'): https://api.slack.com/authentication/token-types
        "|(?:xox[abeprs]|xapp)-[A-Za-z0-9-]{8,}(?![A-Za-z0-9-])" +
        // GitHub ghp_/gho_/ghu_/ghs_/ghr_ (letters and digits): https://github.blog/engineering/platform-security/behind-githubs-new-authentication-token-formats/
        "|gh[pousr]_[A-Za-z0-9]{20,}\\b" +
        // GitHub fine-grained github_pat_ (letters, digits, '_')
        "|github_pat_[A-Za-z0-9_]{22,}(?![A-Za-z0-9_])" +
        // AWS access key id (long-term AKIA, temporary ASIA), 20 chars: https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_identifiers.html#identifiers-unique-ids
        "|(?:AKIA|ASIA)[A-Z0-9]{16}\\b" +
        // Stripe live secret and restricted keys: https://docs.stripe.com/keys
        "|[rs]k_live_[A-Za-z0-9]{16,}\\b" +
        // Stripe webhook signing secret: https://docs.stripe.com/webhooks
        "|whsec_[A-Za-z0-9]{16,}\\b" +
        // Google API key, "AIza" + 35 of letters, digits, '_' and '-': https://cloud.google.com/docs/authentication/api-keys
        "|AIza[A-Za-z0-9_-]{35}(?![A-Za-z0-9_-])" +
        // npm access token, "npm_" + 36 (length per GitHub secret scanning): https://docs.github.com/en/code-security/secret-scanning/introduction/supported-secret-scanning-patterns
        "|npm_[A-Za-z0-9]{36}\\b" +
        // PyPI API token, a macaroon that always begins with the encoded "pypi.org" location: https://pypi.org/help/#apitoken
        "|pypi-AgEIcHlwaS5vcmc[A-Za-z0-9_-]{50,}(?![A-Za-z0-9_-])" +
        // Discord bot token, base64(user id) . timestamp . HMAC (shape per GitHub secret scanning): https://discord.com/developers/docs/reference#authentication
        "|[MNO][A-Za-z0-9_-]{23,25}\\.[A-Za-z0-9_-]{6}\\.[A-Za-z0-9_-]{27,38}(?![A-Za-z0-9_-])" +
        ")")]
    private static partial Regex KnownTokenRegex();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{10,}\\.eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\b")]
    private static partial Regex JwtTokenRegex();

    [GeneratedRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]+?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlockRegex();
}
