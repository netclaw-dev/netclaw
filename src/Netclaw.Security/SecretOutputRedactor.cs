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
        "passwd",
        "passphrase",
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
                normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            || EndsWithShortSecretWord(normalized);
    }

    // "pwd" and "cookie" are short, and ordinary names start with them (pwd_dir, cookie_consent,
    // cookie_jar). Thus they are secret only at the end of a name. PWD and OLDPWD are the shell
    // variables that hold the working directory, so these two names are not secret.
    private static readonly string[] SecretKeySuffixes = ["pwd", "cookie", "cookies"];

    private static bool EndsWithShortSecretWord(string normalized)
        => !normalized.Equals("pwd", StringComparison.OrdinalIgnoreCase)
           && !normalized.Equals("oldpwd", StringComparison.OrdinalIgnoreCase)
           && SecretKeySuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

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

        sanitized = ConnectionStringPasswordRegex.Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted};");

        sanitized = EnvSecretValueRegex.Replace(sanitized, m =>
            $"{m.Groups[1].Value}={Redacted}");

        var beforePrefixed = sanitized;
        sanitized = PrefixedEnvSecretValueRegex().Replace(beforePrefixed, m => RedactPrefixedValue(beforePrefixed, m));

        sanitized = HeaderSecretValueRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted}");

        sanitized = SlackWebhookUrlRegex().Replace(sanitized, m =>
            $"{m.Groups[1].Value}{Redacted}");

        sanitized = KnownTokenRegex().Replace(sanitized, Redacted);

        sanitized = JwtTokenRegex.Replace(sanitized, Redacted);

        sanitized = PrivateKeyBlockRegex.Replace(sanitized, Redacted);

        // The rules below run after the private-key rule. A rule that runs before it can mask
        // a part of an END line, and then the key body stays visible. The rules below also
        // keep all text outside the secret value, so they only add masks to the output above.
        sanitized = UrlCredentialsRegex.Replace(sanitized, m => MaskGroup(m, m.Groups["value"]));

        sanitized = AuthorizationSchemeRegex().Replace(sanitized, m => MaskGroup(m, m.Groups["value"]));

        sanitized = SecretHeaderLineRegex.Replace(sanitized, m => MaskGroup(m, m.Groups["value"]));

        sanitized = CookieHeaderLineRegex.Replace(sanitized, m => MaskGroup(m, m.Groups["value"]));

        sanitized = JsonSecretHeaderRegex.Replace(sanitized, m => MaskGroup(m, m.Groups["value"]));

        sanitized = NameValuePairRegex.Replace(sanitized, m =>
            IsSecretPairName(m.Groups["name"].Value) ? MaskGroup(m, m.Groups["value"]) : m.Value);

        sanitized = SecretOptionValueRegex.Replace(sanitized, RedactOptionValue);

        return sanitized;
    }

    // Replaces only the value group. All other text of the match stays as it is.
    // A value that is already masked, a variable reference, or a help placeholder stays too.
    private static string MaskGroup(Match m, Group value)
    {
        if (!value.Success || value.Length == 0)
            return m.Value;

        var text = value.Value.AsSpan();
        if (text.SequenceEqual(Redacted) || IsReferenceOrPlaceholder(text))
            return m.Value;

        var start = value.Index - m.Index;
        return string.Concat(m.Value.AsSpan(0, start), Redacted, m.Value.AsSpan(start + value.Length));
    }

    // A value that does not hold a secret: a variable or template reference ($X, ${X}, $(cmd),
    // %X%, {{ x }}), a help placeholder (<token>, [token], PASSWORD, API_KEY), or a literal
    // true/false/null/none.
    private static bool IsReferenceOrPlaceholder(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return true;

        if (value[0] is '$' or '<' or '[' or '{')
            return true;

        if (value.Length > 2 && value[0] == '%' && value[^1] == '%' && IsMetaVariable(value[1..^1]))
            return true;

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("null", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsMetaVariable(value);
    }

    // An upper-case name with no digit, such as PASSWORD or API_KEY. Help text uses this form for
    // a value that the user must supply.
    private static bool IsMetaVariable(ReadOnlySpan<char> value)
    {
        if (!char.IsAsciiLetterUpper(value[0]))
            return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterUpper(c) && c is not '_' and not '-')
                return false;
        }

        return true;
    }

    // HAR files and Postman collections keep a header or a variable as a name and a value. The
    // name must end with a secret word. A plain "contains" rule hides pagination cursors
    // (next_page_token) and counters (max_tokens).
    private static readonly string[] SecretPairNameSuffixes =
    [
        "apikey", "token", "secret", "password", "passwd", "passphrase", "authorization",
        "credential", "credentials", "privatekey", "signingkey", "connectionstring", "cookie", "cookies"
    ];

    private static readonly string[] PaginationTokenSuffixes = ["nexttoken", "pagetoken", "continuationtoken", "cursortoken", "startingtoken"];

    private static bool IsSecretPairName(string name)
    {
        if (name.Length > MaxSecretKeyChars)
            return true;

        var normalized = Netclaw.Tools.ToolArgumentHelper.NormalizeKey(name);
        return SecretPairNameSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
               && !PaginationTokenSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    // Prose names an option and then continues the sentence: "pass --token to the CLI",
    // "the --password option". The next word is then not a value.
    private static readonly HashSet<string> ProseWords = new(
    [
        "a", "an", "the", "to", "and", "or", "for", "with", "from", "if", "is", "as", "in", "on", "of",
        "when", "then", "so", "but", "not", "instead", "flag", "option", "argument", "parameter", "switch", "value"
    ], StringComparer.Ordinal);

    // The option word list is in SecretOptionValueRegex. "--no-password" is a flag with no
    // value, and a pagination cursor (--next-token, --starting-token) is not a secret.
    private static string RedactOptionValue(Match m)
    {
        var name = m.Groups["name"].Value;
        var normalizedName = Netclaw.Tools.ToolArgumentHelper.NormalizeKey(name);
        if (name.StartsWith("no-", StringComparison.OrdinalIgnoreCase)
            || PaginationTokenSuffixes.Any(suffix => normalizedName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return m.Value;
        }

        var value = m.Groups["value"];
        var core = value.Value.AsSpan();
        if (core.Length >= 2 && core[0] is '"' or '\'' && core[^1] == core[0])
        {
            // A quoted value keeps its quotes, so the command stays readable.
            var inner = core[1..^1];
            if (inner.IsEmpty || IsReferenceOrPlaceholder(inner))
                return m.Value;

            var start = value.Index - m.Index;
            return string.Concat(m.Value.AsSpan(0, start + 1), Redacted, m.Value.AsSpan(start + value.Length - 1));
        }

        if (ProseWords.Contains(value.Value))
            return m.Value;

        return MaskGroup(m, value);
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
    private const string SecretNameWords = "api[_-]?key|token|secret|password|passwd|passphrase|authorization|access[_-]?token|refresh[_-]?token|client[_-]?secret|signing[_-]?key|private[_-]?key|connection[_-]?string|credential";

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

    // Redact runs on every tool result, on inputs up to megabytes, so a pattern must not rescan
    // from each candidate start to a terminator that may never come ("token-token-...", a BEGIN
    // PRIVATE KEY with no END). The backtracking engine is quadratic on those; NonBacktracking
    // matches in time linear in the input. It rules out lookarounds, so the rules that need
    // them (PrefixedEnvSecretValueRegex, KnownTokenRegex) stay source-generated with bounded
    // quantifiers instead.
    private static Regex LinearRegex(string pattern, RegexOptions options = RegexOptions.IgnoreCase)
        => new(pattern, options | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    [GeneratedRegex("\"((?:" + SecretNameWords + ")[^\"]*)\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretValueRegex();

    // Kept in lockstep with JsonSecretValueRegex's key words: an OAuth token endpoint's
    // error body is as likely to arrive form-urlencoded ("client_secret=...&grant_type=...")
    // as JSON. The secret word must sit at the start of a name (\b), e.g. TOKEN_FILE, --password.
    private static readonly Regex EnvSecretValueRegex = LinearRegex("\\b((?:" + SecretNameWords + ")[A-Z0-9_-]*)=([^\\s;]+)");

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

    private static readonly Regex ConnectionStringPasswordRegex = LinearRegex("((?:Password|Pwd)\\s*=\\s*)[^;]+;");

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

    private static readonly Regex JwtTokenRegex = LinearRegex("\\beyJ[A-Za-z0-9_-]{10,}\\.eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\b", RegexOptions.None);

    private static readonly Regex PrivateKeyBlockRegex = LinearRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]+?-----END [A-Z ]*PRIVATE KEY-----");

    // The password in the userinfo part of a URL: scheme://user:password@host. The user name
    // can be empty (redis://:pw@host). The password can contain ':' (RFC 3986). The user name
    // cannot contain '/', so a port and a path ("host:4873/@scope/pkg") do not match.
    private static readonly Regex UrlCredentialsRegex = LinearRegex(
        "://[^\\s/?#@:\"'<>\\[\\]\\\\]*:(?<value>[^\\s/?#@\"'<>\\\\]+)@", RegexOptions.None);

    // An Authorization or Proxy-Authorization header with a known scheme. The scheme stays, so
    // the reader can see the kind of credential. "Authorization: required" has no scheme and stays.
    // In the 4 MB timing tests, the NonBacktracking form of this rule ran past the budget. This rule
    // starts only at the literal "Authorization", and each run is atomic, so an attempt reads
    // each character once and never gives a character back.
    [GeneratedRegex("Authorization(?>[ \\t]*):(?>[ \\t]*)(?>Basic|Bearer|Token|Bot|Digest|Negotiate|NTLM|ApiKey|Key)(?>[ \\t]+)(?<value>(?>[^\\s\"'\\\\]+))", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationSchemeRegex();

    // A header line whose name ends with "token" or "api-key": X-Api-Key, X-Auth-Token,
    // X-Vault-Token. The name must start a line (after an optional curl "> " or "< " marker) or
    // follow a quote (curl -H "..."). Thus "Allow-Headers: X-Api-Key, ..." and prose stay.
    private static readonly Regex SecretHeaderLineRegex = LinearRegex(
        "(?:^(?:[<>][ \\t]*)?|[\"'])[ \\t]*(?:[A-Za-z0-9]+-)*(?:api-?key|token)[ \\t]*:[ \\t]*(?<value>[^\\s\"'\\\\]+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // A Cookie or Set-Cookie header line. All of the value is masked, to the end of the line or
    // the quote. The value must hold a "name=value" pair, so "Cookie policy: ..." stays.
    private static readonly Regex CookieHeaderLineRegex = LinearRegex(
        "(?:^(?:[<>][ \\t]*)?|[\"'])[ \\t]*(?:Set-)?Cookie[ \\t]*:[ \\t]*(?<value>[^\\r\\n\"'=]+=[^\\r\\n\"']*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // The same header names as JSON keys: {"X-Api-Key": "..."}. The name must follow the quote
    // directly, so "next_page_token" and "progressToken" do not match.
    private static readonly Regex JsonSecretHeaderRegex = LinearRegex(
        "\"(?:(?:[A-Za-z0-9]+-)*(?:api-?key|token)|(?:Set-)?Cookie|Proxy-Authorization)\"\\s*:\\s*\"(?<value>[^\"]+)\"");

    // A name and value pair: {"name":"Authorization","value":"..."} or {"key":"api_key","value":"..."}.
    // IsSecretPairName decides on the name in code.
    private static readonly Regex NameValuePairRegex = LinearRegex(
        "\"(?:name|key)\"\\s*:\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"value\"\\s*:\\s*\"(?<value>[^\"]+)\"");

    // A secret option and its value after a space: --password x, --api-key x, --client-secret x.
    // The option name must end with the secret word, so --password-file and --password-stdin do
    // not match. A bare "--secret" names a secret spec for some tools (docker build), so "secret"
    // must have a word before it. The value cannot cross a line end and cannot start with '-'.
    // "-p" is not covered: it is a port, a path flag, or a database name for many tools.
    private static readonly Regex SecretOptionValueRegex = LinearRegex(
        "--(?<name>(?:[A-Za-z0-9]+-)*(?:password|passwd|passphrase|token|api-?key)|(?:[A-Za-z0-9]+-)+secret)[ \\t]+"
        + "(?<value>\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s\"'-][^\\s]*)");
}
