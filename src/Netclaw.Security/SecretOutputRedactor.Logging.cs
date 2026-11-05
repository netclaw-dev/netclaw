// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactor.Logging.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers;
using System.Text.RegularExpressions;

namespace Netclaw.Security;

// Log redaction: the shared rules, then the log-only rules. A log is a terminal sink: a person
// or a tool reads it but does not act on it. Thus a log can mask a value that an operative sink
// must keep, such as a presigned URL signature that a model uses to upload a file (#1992).
// Keep each log-only rule out of Redact, which operative sinks use.
public static partial class SecretOutputRedactor
{
    /// <summary>
    /// Redacts text for a log sink: the shared rules of <see cref="Redact(string)"/>, then the
    /// log-only rules. Every log writer uses this method. Do not use it for a value that a model,
    /// a tool, or a person acts on: the log-only rules mask values that such a receiver needs.
    /// </summary>
    public static string RedactForLogging(string text) => RedactForLogging(text, masks: null);

    /// <summary>
    /// Returns one flag for each character of <paramref name="text"/>. A flag is true when
    /// <see cref="RedactForLogging(string)"/> removes or replaces that character.
    /// </summary>
    public static bool[] FindLogMasks(string text)
    {
        var masks = new LogMaskMap(text.Length);
        RedactForLogging(text, masks);
        return masks.Masked;
    }

    private static string RedactForLogging(string text, LogMaskMap? masks)
    {
        var sanitized = Redact(text, masks);
        if (string.IsNullOrEmpty(sanitized))
            return sanitized;

        // Each log-only rule runs only when its literal is in the text, so an ordinary record
        // pays for two vectorized scans.
        if (sanitized.AsSpan().IndexOfAny(LineBreaks) >= 0)
        {
            var keyText = sanitized;
            sanitized = Replace(NextLineValueRegex, keyText, m => RedactNextLineValue(keyText, m), masks);
        }

        if (sanitized.Contains("sig", StringComparison.OrdinalIgnoreCase))
            sanitized = Replace(SignatureParameterRegex, sanitized, static m => MaskGroup(m, m.Groups["value"]), masks);

        return sanitized;
    }

    // The line breaks that LogTextFraming.Normalize turns into new lines: CR, LF, FF, NEL,
    // U+2028, and U+2029.
    private static readonly SearchValues<char> LineBreaks =
        SearchValues.Create(['\r', '\n', '\f', (char)0x85, (char)0x2028, (char)0x2029]);

    // Log-only. A key at the end of a line and its value at the start of the next line:
    // "API_KEY=" and then "abc123" (#2770). The match starts at the line break and does not
    // include the '='. The code reads the key from the text before the line break. Thus a
    // token that is a key stays out of the value, and the next pair is read on its own:
    // "API_KEY=", "DB_PASSWORD=", and then "hunter2" masks only hunter2. A "Name:" line is not
    // covered: prose such as "Failed to refresh token:" followed by a stack trace would lose
    // its first frame.
    private static readonly Regex NextLineValueRegex = LinearRegex(
        "(?:\\r\\n|[\\r\\n\\f\\x85\\p{Zl}\\p{Zp}])[ \\t]*(?<value>[^\\s;]+)", RegexOptions.None);

    // The value is masked only when the shared rules mask the same key and value on one line.
    // Thus this rule has the vocabulary and the exceptions of the shared rules: max_tokens,
    // next_token, password_file, a $VARIABLE, and so on stay as they are.
    private static string RedactNextLineValue(string text, Match m)
    {
        var end = m.Index;
        while (end > 0 && text[end - 1] is ' ' or '\t')
            end--;

        if (end == 0 || text[end - 1] != '=')
            return m.Value;

        var value = m.Groups["value"];
        if (IsKeyToken(value.ValueSpan))
            return m.Value;

        end--;
        var start = end;
        while (start > 0 && end - start < MaxSecretKeyChars && IsKeyCharacter(text[start - 1]))
            start--;

        if (start == end)
            return m.Value;

        var joined = string.Concat(text.AsSpan(start, end - start), "=", value.ValueSpan);
        return string.Equals(Redact(joined), joined, StringComparison.Ordinal)
            ? m.Value
            : MaskGroup(m, value);
    }

    // A token that starts a new pair: "DB_PASSWORD=" or "HOME=/root". It is not the value of the
    // key before it. Base64 padding ("abc123==") and a token with a character that a key does not
    // have ("a/b+c=") are values.
    private static bool IsKeyToken(ReadOnlySpan<char> token)
    {
        var equals = token.IndexOf('=');
        if (equals <= 0)
            return false;

        if (equals + 1 < token.Length && token[equals + 1] == '=')
            return false;

        foreach (var c in token[..equals])
        {
            if (!IsKeyCharacter(c))
                return false;
        }

        return true;
    }

    // The characters of an environment, option, or query key: API_KEY, --api-key, app.secret.
    private static bool IsKeyCharacter(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';

    // Log-only. The signature of a presigned URL, in any position of the query: S3
    // X-Amz-Signature, GCS X-Goog-Signature, S3 v2 and CloudFront Signature, and Azure SAS sig.
    // The shared rules mask X-Amz-Credential to the end of the query, so a signature after the
    // credential is masked there, but a signature before it is not (#2770). ';' lets "&amp;sig="
    // in HTML match.
    private static readonly Regex SignatureParameterRegex = LinearRegex(
        "[?&;](?:(?:X-Amz-|X-Goog-)?Signature|sig)=(?<value>[^&\\s#\"'<>\\\\]+)");

    /// <summary>
    /// Records which characters of an original text the rules mask. Each rule runs on the output
    /// of the rule before it, so the map keeps, for each character of the current text, the index
    /// of the original character that it came from, or -1 for mask text. A replacement keeps the
    /// text that it shares with its match at the start and at the end. The original characters
    /// between those parts are masked.
    /// </summary>
    private sealed class LogMaskMap
    {
        private readonly List<Edit> _edits = [];
        private int[] _origin;

        public LogMaskMap(int length)
        {
            Masked = new bool[length];
            _origin = new int[length];
            for (var i = 0; i < length; i++)
                _origin[i] = i;
        }

        public bool[] Masked { get; }

        public string Replace(Regex rule, string input, MatchEvaluator evaluator)
        {
            _edits.Clear();
            // Regex.Replace calls the evaluator once for each match, from left to right.
            var output = rule.Replace(input, m =>
            {
                var replacement = evaluator(m);
                var original = m.ValueSpan;
                if (!original.SequenceEqual(replacement))
                {
                    var prefix = original.CommonPrefixLength(replacement);
                    var suffix = CommonSuffixLength(original[prefix..], replacement.AsSpan(prefix));
                    _edits.Add(new Edit(m.Index, m.Length, prefix, suffix, replacement.Length));
                }

                return replacement;
            });

            if (_edits.Count > 0)
                Apply(input.Length, output.Length);

            return output;
        }

        private void Apply(int inputLength, int outputLength)
        {
            var origin = new int[outputLength];
            var read = 0;
            var write = 0;
            foreach (var edit in _edits)
            {
                var kept = edit.Index + edit.Prefix - read;
                Array.Copy(_origin, read, origin, write, kept);
                read += kept;
                write += kept;

                var removed = edit.Length - edit.Prefix - edit.Suffix;
                for (var i = read; i < read + removed; i++)
                {
                    if (_origin[i] >= 0)
                        Masked[_origin[i]] = true;
                }

                read += removed;
                var inserted = edit.ReplacementLength - edit.Prefix - edit.Suffix;
                origin.AsSpan(write, inserted).Fill(-1);
                write += inserted;

                Array.Copy(_origin, read, origin, write, edit.Suffix);
                read += edit.Suffix;
                write += edit.Suffix;
            }

            Array.Copy(_origin, read, origin, write, inputLength - read);
            write += inputLength - read;
            if (write != outputLength)
                throw new InvalidOperationException("The log mask map does not match the redacted text.");

            _origin = origin;
        }

        private static int CommonSuffixLength(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
        {
            var length = 0;
            while (length < left.Length && length < right.Length && left[^(length + 1)] == right[^(length + 1)])
                length++;

            return length;
        }

        private readonly record struct Edit(int Index, int Length, int Prefix, int Suffix, int ReplacementLength);
    }
}
