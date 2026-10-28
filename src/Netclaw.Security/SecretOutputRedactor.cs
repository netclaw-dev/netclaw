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

        var headerText = sanitized;
        sanitized = SecretHeaderLineRegex.Replace(headerText, m => RedactHeaderValue(headerText, m));

        var jsonText = sanitized;
        sanitized = JsonSecretHeaderRegex.Replace(jsonText, m => RedactHeaderValue(jsonText, m));

        sanitized = NameValuePairRegex.Replace(sanitized, m =>
            IsSecretPairName(m.Groups["name"].Value) ? MaskGroup(m, m.Groups["value"]) : m.Value);

        var optionText = sanitized;
        sanitized = SecretOptionValueRegex.Replace(optionText, m => RedactOptionValue(optionText, m));

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
    // %X% in any case, {{ x }}), a help placeholder (<token>, [token], PASSWORD, API_KEY), or a
    // literal true/false/null/none.
    private static bool IsReferenceOrPlaceholder(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return true;

        if (value[0] is '$' or '<' or '[' or '{')
            return true;

        if (value.Length > 2 && value[0] == '%' && value[^1] == '%' && IsVariableName(value[1..^1]))
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

    // The name of a %x% reference, the same shape that PrefixedEnvSecretValueRegex skips.
    private static bool IsVariableName(ReadOnlySpan<char> value)
    {
        if (!char.IsAsciiLetter(value[0]) && value[0] != '_')
            return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
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

    // Prose names a secret word and then goes on: "--token must be a PAT", "Token: required for
    // all calls", "token: expired, please refresh". A value is prose when it is a ProseWords
    // word. A value of at most 12 lower-case letters is also prose when it ends with ',', '.' or
    // ':', or when a ProseWords word comes after it. Other words after it are more arguments: in
    // "mysql --password changeme mydb", "changeme" is a password. ';' ends a shell command.
    private static bool IsProse(ReadOnlySpan<char> value, ReadOnlySpan<char> nextWord)
    {
        var word = value.TrimEnd(".,:");
        if (word.IsEmpty || word.Length > MaxProseWordChars)
            return false;

        if (ProseWordLookup.Contains(word))
            return true;

        return !word.ContainsAnyExceptInRange('a', 'z')
               && (word.Length < value.Length || ProseWordLookup.Contains(nextWord));
    }

    private const int MaxProseWordChars = 12;

    // Function words, sentence verbs, and the value types that cobra and other CLI help
    // generators print ("--token string   Bearer token for ...").
    private static readonly HashSet<string> ProseWords = new(
    [
        "a", "an", "the", "be", "is", "are", "was", "were", "has", "it", "to", "for", "of", "in", "on",
        "with", "from", "and", "or", "if", "as", "when", "then", "so", "but", "not", "over", "instead",
        "please", "should", "can", "will", "must", "takes", "flag", "option", "argument", "parameter",
        "switch", "value", "string", "strings", "stringArray", "int", "bool", "duration", "file", "path"
    ], StringComparer.Ordinal);

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ProseWordLookup =
        ProseWords.GetAlternateLookup<ReadOnlySpan<char>>();

    // The lower-case word after a match, after spaces and tabs. It reads at most one letter more
    // than MaxProseWordChars, so a longer word is not a ProseWords word. A scan to the end of the
    // line for each match is quadratic on a long line with many matches.
    private static ReadOnlySpan<char> NextWord(string text, Match m)
    {
        var rest = text.AsSpan(m.Index + m.Length);
        var skip = rest.IndexOfAnyExcept(' ', '\t');
        if (skip < 0)
            return ReadOnlySpan<char>.Empty;

        rest = rest.Slice(skip, Math.Min(MaxProseWordChars + 1, rest.Length - skip));
        var end = rest.IndexOfAnyExceptInRange('a', 'z');
        return end < 0 ? rest : rest[..end];
    }

    // The last segment of a name, and the text before it. Segments are split at '_', '-', '.', a
    // space, and a change from a lower-case letter or a digit to an upper-case letter:
    // next_page_token, nextPageToken, Next-Page-Token. It reads from the end, so a long name
    // costs only its last segments.
    private static ReadOnlySpan<char> LastSegment(ReadOnlySpan<char> name, out ReadOnlySpan<char> before)
    {
        var end = name.Length;
        while (end > 0 && name[end - 1] is '_' or '-' or '.' or ' ')
            end--;

        var start = end;
        while (start > 0
               && name[start - 1] is not ('_' or '-' or '.' or ' ')
               && !(start < end && char.IsAsciiLetterUpper(name[start])
                    && (char.IsAsciiLetterLower(name[start - 1]) || char.IsAsciiDigit(name[start - 1]))))
        {
            start--;
        }

        before = name[..start];
        return name[start..end];
    }

    // A pagination cursor is not a secret: next_token, next_page_token, --starting-token. The
    // words are PaginationWords and "starting". PrefixedEnvSecretValueRegex does not skip
    // "starting", because the released redactor masked STARTING_TOKEN=x.
    private static readonly HashSet<string> PaginationNames =
        new([.. PaginationWords.Split('|'), "starting"], StringComparer.OrdinalIgnoreCase);

    private static bool IsPaginationName(string name)
    {
        var last = LastSegment(name, out var before);
        return last.Equals("token", StringComparison.OrdinalIgnoreCase)
               && PaginationNames.GetAlternateLookup<ReadOnlySpan<char>>().Contains(LastSegment(before, out _));
    }

    // The last segment of a pair name that names a thing about the secret, not the secret:
    // secret_name, password_file, token_count. These are the DescriptorWords without "header":
    // a pair named AuthorizationHeader holds the header value.
    private static readonly HashSet<string> PairDescriptorWords =
        new(DescriptorWords.Split('|').Where(word => word != "header"), StringComparer.OrdinalIgnoreCase);

    // A plural "tokens" after one of these words is a count (max_tokens, prompt_tokens). After
    // another word it is a list of credentials (api_tokens, refresh_tokens).
    private static readonly HashSet<string> CounterWords = new(
        ["max", "min", "num", "total", "input", "output", "prompt", "completion", "cached", "reasoning", "remaining", "used"],
        StringComparer.OrdinalIgnoreCase);

    // HAR files and Postman collections keep a header or a variable as a name and a value. The
    // name uses the vocabulary of IsSecretKey. The last segment then rules out a descriptor, a
    // pagination cursor, and a count.
    private static bool IsSecretPairName(string name)
    {
        if (!IsSecretKey(name))
            return false;

        if (name.Length > MaxSecretKeyChars)
            return true;

        var last = LastSegment(name, out var before);
        var previous = LastSegment(before, out _);
        return !PairDescriptorWords.GetAlternateLookup<ReadOnlySpan<char>>().Contains(last)
               && !(last.Equals("token", StringComparison.OrdinalIgnoreCase) && PaginationNames.GetAlternateLookup<ReadOnlySpan<char>>().Contains(previous))
               && !(last.Equals("tokens", StringComparison.OrdinalIgnoreCase) && CounterWords.GetAlternateLookup<ReadOnlySpan<char>>().Contains(previous));
    }

    // A header value is masked except in these cases:
    //  - the name is a pagination cursor (Next-Token);
    //  - the value is a short number, which is a count or a limit (X-RateLimit-Token: 5,
    //    input-token: 500). A header credential of five digits or fewer is rare. A count is
    //    common, and a mask of it drops a memory;
    //  - the value is a lower-case word in a sentence (Token: required for all calls), on a
    //    header line;
    //  - the value is a reference or a placeholder (MaskGroup).
    // A Cookie value and the credential after a known Authorization scheme are always values.
    private static string RedactHeaderValue(string text, Match m)
    {
        var name = m.Groups["name"].Value;
        var value = m.Groups["value"];
        if (name.EndsWith("cookie", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("authorization", StringComparison.OrdinalIgnoreCase))
        {
            return MaskGroup(m, value);
        }

        if (IsPaginationName(name))
            return m.Value;

        // Only a header line after "Name: " can be prose. A JSON value (after a quote) can hold
        // spaces, such as a passphrase. "Name:value" with no space after the colon is a header
        // argument (curl -H X-Api-Key:x).
        var span = value.Value.AsSpan();
        var afterSpace = text[value.Index - 1] is ' ' or '\t';
        if (IsShortNumber(span) || (afterSpace && IsProse(span, NextWord(text, m))))
            return m.Value;

        return MaskGroup(m, value);
    }

    private static bool IsShortNumber(ReadOnlySpan<char> value)
        => value.Length <= 5 && !value.ContainsAnyExcept("0123456789");

    // The option word list is in SecretOptionValueRegex. "--no-password" is a flag with no
    // value, and a pagination cursor (--next-token, --starting-token) is not a secret.
    private static string RedactOptionValue(string text, Match m)
    {
        var name = m.Groups["name"].Value;
        if (name.StartsWith("no-", StringComparison.OrdinalIgnoreCase)
            || IsPaginationName(name))
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

        // "--token (or GH_TOKEN)", "| --password | ... |" and "--token `<your token>`" have no value.
        if (core[0] is '(' or '|' or '`' || IsProse(core, NextWord(text, m)))
        {
            return m.Value;
        }

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

    /// <summary>
    /// Redacts secrets, then forces the message onto one line.
    /// </summary>
    /// <remarks>
    /// Use this only where the exception text comes from a remote server that can write
    /// arbitrary content into its error body. Such a body can hold a line break, and a line
    /// break lets the far side add a forged entry to the log (CWE-117), which
    /// <see cref="RedactForLogging"/> does not prevent because it only masks secrets.
    /// <para>
    /// Caution: the returned exception replaces the original, so the native stack trace is
    /// gone. Do not use this on a local failure that you need to debug.
    /// </para>
    /// </remarks>
    public static Exception RedactForLoggingOnOneLine(Exception ex, int maxChars = 2000)
    {
        var redacted = RedactForLogging(ex);
        var typeName = ex.GetType().FullName ?? ex.GetType().Name;
        return new RedactedLoggingException($"{typeName}: {LogSafe.Text(redacted.Message, maxChars)}");
    }

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

    // Pagination cursors and descriptors. PrefixedEnvSecretValueRegex uses them in its
    // lookbehinds, and the header, option, and pair rules use them in code.
    private const string PaginationWords = "next|page|continuation|cursor";
    private const string DescriptorWords = "file|path|dir|name|arn|url|header|env|permissions|length|lifetime|endpoint|stdin|attempts|count";

    // The same words after an underscore: DB_PASSWORD, AWS_SECRET_ACCESS_KEY, GITHUB_TOKEN.
    // Narrower than the rule above because ordinary settings carry these words too:
    // the word must be a whole segment (so not max_tokens, TOKENIZERS_*), must not follow
    // a PaginationWords word (pagination cursors), the name must not end in a
    // descriptor (TOKEN_FILE, SECRET_ARN, PASSWORD_MIN_LENGTH, PASSWORD_STDIN, PASSWORD_ATTEMPTS ...), and a value that is a
    // variable or template reference (${{ secrets.X }}, ${X}, $X, $(cmd), %X%, optionally quoted) or an
    // empty quoted value is not a secret.
    // Only the secret word onwards is matched, so the prefix is left untouched. The tail is
    // bounded so a long run of "token_token_..." stays linear.
    [GeneratedRegex("(?<=_)(?<!(?:" + PaginationWords + ")_)((?:" + SecretNameWords + ")(?![A-Z])(?>[A-Z0-9_-]{0,64}))"
        + "(?<!_(?:" + DescriptorWords + "))"
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
    // can be empty (redis://:pw@host) and can hold '@' (an e-mail address). The password can
    // hold ':' (RFC 3986) and up to two '@'. The '@' count is bounded: an unbounded run that
    // ends at '@' took more than 20 s on 4 MB of "b@". The user name cannot hold '/', so a
    // port and a path ("host:4873/@scope/pkg") do not match.
    private static readonly Regex UrlCredentialsRegex = LinearRegex(
        "://[^\\s/?#:\"'<>\\[\\]\\\\]*:(?<value>[^\\s/?#@\"'<>\\\\]+(?:@[^\\s/?#@\"'<>\\\\]+){0,2})@", RegexOptions.None);

    // The header names that carry a credential. The header-line rule and the JSON key rule
    // use this one list. A token or key name must end with the secret word, so
    // "X-RateLimit-Remaining" and "Token-Count" do not match.
    // A name has at most eight segments before the secret word. Thus a huge hyphen chain does
    // not match, and Redact does not copy it.
    private const string SecretTokenHeaderNames = "(?:[A-Za-z0-9]+-){0,8}(?:api-?key|auth-key|subscription-key|client-secret|token)";
    private const string AuthorizationHeaderNames = "(?:Proxy-)?Authorization";
    private const string CookieHeaderNames = "(?:Set-)?Cookie";
    private const string SecretHeaderNames = SecretTokenHeaderNames + "|" + AuthorizationHeaderNames + "|" + CookieHeaderNames;

    // A header line. RedactHeaderValue decides on the value. The forms:
    //  - a name at the start of a line (after an optional curl "> " or "< " marker) or after a
    //    quote (curl -H "..."), with optional spaces around the colon;
    //  - a name after a space or '=' with no space around the colon (curl -H X-Api-Key:x,
    //    wget --header=X-Api-Key:x). Prose such as "Input token: 512" has a space;
    //  - Authorization or Proxy-Authorization with a known scheme, anywhere. The scheme stays;
    //  - Cookie or Set-Cookie at the start of a line or after a quote. All of the value is
    //    masked to the end of the line or the quote. It must hold a "name=value" pair, so
    //    "Cookie policy: ..." stays.
    // "Access-Control-Allow-Headers: Authorization, X-Api-Key" has no colon after the names.
    private static readonly Regex SecretHeaderLineRegex = LinearRegex(
        "(?:^(?:[<>][ \\t]*)?[ \\t]*|[\"'])(?<name>" + SecretTokenHeaderNames + ")[ \\t]*:[ \\t]*(?<value>[^\\s\"'\\\\]+)"
        + "|[\\s=](?<name>" + SecretTokenHeaderNames + "):(?<value>[^\\s\"'\\\\]+)"
        + "|(?<name>" + AuthorizationHeaderNames + ")[ \\t]*:[ \\t]*(?:Basic|Bearer|Token|Bot|Digest|Negotiate|NTLM|Api-?Key|Key)[ \\t]+(?<value>[^\\s\"'\\\\]+)"
        + "|(?:^(?:[<>][ \\t]*)?[ \\t]*|[\"'])(?<name>" + CookieHeaderNames + ")[ \\t]*:[ \\t]*(?<value>[^\\r\\n\"'=]+=[^\\r\\n\"']*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // The same header names as JSON keys: {"X-Api-Key": "..."}. The name must follow the quote
    // directly, so "next_page_token" and "progressToken" do not match.
    // CAUTION: keep '*' (not '+') before each closing quote here and in NameValuePairRegex. In
    // NonBacktracking mode a '+' run before a closing quote made the match-bounds step read about
    // 5 microseconds for each character: 24 s for one 4 MB value. The code accepts an empty value.
    private static readonly Regex JsonSecretHeaderRegex = LinearRegex(
        "\"(?<name>" + SecretHeaderNames + ")\"\\s*:\\s*\"(?<value>[^\"]*)\"");

    // A name and value pair: {"name":"Authorization","value":"..."} or {"key":"api_key","value":"..."}.
    // IsSecretPairName decides on the name in code.
    private static readonly Regex NameValuePairRegex = LinearRegex(
        "\"(?:name|key)\"\\s*:\\s*\"(?<name>[^\"]*)\"\\s*,\\s*\"value\"\\s*:\\s*\"(?<value>[^\"]*)\"");

    // A secret option and its value after a space: --password x, --api-key x, --client-secret x.
    // The option name must end with the secret word, so --password-file and --password-stdin do
    // not match. A bare "--secret" names a secret spec for some tools (docker build), so "secret"
    // must have a word before it. As in the header names, at most eight words come before the
    // secret word. The value cannot cross a line end and cannot start with '-'.
    // "-p" is not covered: it is a port, a path flag, or a database name for many tools.
    private static readonly Regex SecretOptionValueRegex = LinearRegex(
        "--(?<name>(?:[A-Za-z0-9]+-){0,8}(?:password|passwd|passphrase|token|api-?key)|(?:[A-Za-z0-9]+-){1,8}secret)[ \\t]+"
        + "(?<value>\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s\"'-][^\\s]*)");
}
