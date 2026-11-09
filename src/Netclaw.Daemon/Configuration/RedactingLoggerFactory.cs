// -----------------------------------------------------------------------
// <copyright file="RedactingLoggerFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// The daemon's one <see cref="ILoggerFactory"/>. Every logger in the daemon comes from it:
/// <c>ILogger&lt;T&gt;</c>, explicit <c>CreateLogger</c> calls, Akka through
/// <c>AddLoggerFactory()</c>, and framework components. Every provider sits behind
/// <paramref name="inner"/>, so a provider that is registered later is behind this factory
/// too, with no extra step.
/// </summary>
internal sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// Redacts a copy of each enabled record before any provider sees it: the formatted message,
/// each structured value, each scope value, and the exception. The caller's state and
/// exception are never changed, because MEL hands the same instances to every provider and
/// the caller may still use them. Attribute names are kept, so routing by key still works and
/// a name such as <c>InputTokens</c> does not hide its value.
/// </summary>
internal sealed class RedactingLogger(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => inner.BeginScope(RedactScope(state));

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // A disabled level never pays for redaction.
        if (!inner.IsEnabled(logLevel))
            return;

        RedactedLogState redacted;
        Exception? redactedException;
        try
        {
            redacted = RedactState(formatter(state, exception), state as IEnumerable<KeyValuePair<string, object?>>);
            redactedException = exception is null ? null : RedactException(exception);
        }
        catch (Exception failure)
        {
            // Fail closed: a record that could not be redacted never reaches a sink.
            redacted = new RedactedLogState(
                $"[log record withheld: redaction failed: {failure.GetType().Name}]",
                []);
            redactedException = null;
        }

        inner.Log(logLevel, eventId, redacted, redactedException, static (s, _) => s.Message);
    }

    private static object RedactScope(object state)
    {
        try
        {
            return state switch
            {
                string text => RedactText(text),
                IEnumerable<KeyValuePair<string, object?>> pairs => RedactState(state.ToString() ?? string.Empty, pairs),
                _ => RedactText(state.ToString() ?? string.Empty),
            };
        }
        catch (Exception failure)
        {
            // Fail closed, and do not throw into the code that opened the scope.
            return $"[log scope withheld: redaction failed: {failure.GetType().Name}]";
        }
    }

    private static RedactedLogState RedactState(string message, IEnumerable<KeyValuePair<string, object?>>? pairs)
    {
        var redactedMessage = SecretOutputRedactor.RedactForLogging(message);
        var values = RedactValues(pairs);

        // Only a message that holds a secret can have a value that its template marks as a
        // secret. Thus an ordinary record pays for one string comparison here.
        if (pairs is not null && !string.Equals(redactedMessage, message, StringComparison.Ordinal))
            TemplateMarkedValues.Mask(pairs, values);

        return new(LogTextFraming.Normalize(redactedMessage), values);
    }

    // Each value is redacted alone. TemplateMarkedValues then masks a value whose secret marker
    // sits in the template, as in "Authorization: Bearer {Token}" (#2770).
    private static KeyValuePair<string, object?>[] RedactValues(IEnumerable<KeyValuePair<string, object?>>? pairs)
        => pairs is null
            ? []
            : [.. pairs.Select(static pair => new KeyValuePair<string, object?>(pair.Key, RedactValue(pair.Value)))];

    // Numbers, enums, and other convertible primitives cannot carry a secret and keep their
    // type for structured sinks. Anything else is rendered the way MEL renders it and scanned.
    private static object? RedactValue(object? value)
        => value switch
        {
            null => null,
            string text => RedactText(text),
            IConvertible => value,
            IEnumerable items => RedactText(string.Join(", ", items.Cast<object?>())),
            _ => RedactText(value.ToString() ?? string.Empty),
        };

    // RedactForLogging returns the caller's exception when it holds no secret. A text sink still
    // writes its text, and the single-line console formatter keeps a lone CR, an escape
    // sequence, or a bidi control as it is. So an exception whose text needs escaping is
    // replaced by a stand-in that carries the normalized text.
    private static Exception RedactException(Exception exception)
    {
        var redacted = SecretOutputRedactor.RedactForLogging(exception);
        var text = redacted.ToString();
        var normalized = LogTextFraming.Normalize(text);
        return string.Equals(text, normalized, StringComparison.Ordinal)
            ? redacted
            : new NormalizedLogException(LogTextFraming.Normalize(redacted.Message), normalized);
    }

    // Redact first: some secret patterns end at a line break, so they must see the original breaks.
    private static string RedactText(string text) => LogTextFraming.Normalize(SecretOutputRedactor.RedactForLogging(text));
}

/// <summary>
/// Masks a structured value that its message template marks as a secret. In
/// <c>LogInformation("Authorization: Bearer {Token}", token)</c>, redaction of the message masks
/// the token, but redaction of the <c>Token</c> value alone finds no secret shape (#2770).
/// </summary>
/// <remarks>
/// The template is rendered with its values, and each value gets the span of its placeholder in
/// that text. The span comes from the placeholder position, never from a search for the value
/// text: the same text can be a secret at one position and an ordinary word at another. The
/// rendered template stands in for the formatted message, because a formatter can add text that
/// the stage cannot map, such as the Akka prefix. A value is masked when log redaction of the
/// rendered text masks a character of its span that log redaction of the value alone does not.
/// </remarks>
internal static class TemplateMarkedValues
{
    private const string OriginalFormatKey = "{OriginalFormat}";

    // A secret pattern can run past its own value into the next one, as in
    // "token={Token}&session={SessionId}". These values route or correlate a record, and they are
    // never secrets. A masked SessionId sends the record to a session log named ***REDACTED***,
    // and a masked attempt ID breaks the OTLP correlation of #2589.
    private static readonly FrozenSet<string> ExemptKeys = FrozenSet.Create(
        StringComparer.Ordinal,
        NetclawLogProperties.SessionId,
        NetclawLogProperties.SubSessionId,
        "LogSource",
        "ActorPath",
        "AuthorizationAttemptId");

    /// <summary>
    /// Replaces each value in <paramref name="redacted"/> that the template marks as a secret
    /// with the mask text. <paramref name="redacted"/> holds the redacted values of
    /// <paramref name="pairs"/>, in the same order.
    /// </summary>
    public static void Mask(IEnumerable<KeyValuePair<string, object?>> pairs, KeyValuePair<string, object?>[] redacted)
    {
        var originals = pairs.ToArray();
        if (originals.Length != redacted.Length)
            throw new InvalidOperationException("The log state returned a different number of values on a second read.");

        var template = originals.FirstOrDefault(static pair => pair.Key == OriginalFormatKey).Value as string;
        if (string.IsNullOrEmpty(template))
            return;

        var rendered = Render(template, originals, out var spans);
        if (spans.Count == 0)
            return;

        var masks = SecretOutputRedactor.FindLogMasks(rendered);
        foreach (var span in spans)
        {
            var inContext = masks.AsSpan(span.Start, span.Text.Length);
            if (ExemptKeys.Contains(originals[span.PairIndex].Key) || !inContext.Contains(true))
                continue;

            if (IsMaskedOnlyInContext(inContext, span.Text))
                redacted[span.PairIndex] = new KeyValuePair<string, object?>(originals[span.PairIndex].Key, SecretOutputRedactor.Redacted);
        }
    }

    // True when the rendered text masks a character of the value that the value alone keeps.
    // A value that holds the whole secret shape ("Authorization: Bearer abc") is masked the same
    // way alone, so it keeps its own redacted text.
    private static bool IsMaskedOnlyInContext(ReadOnlySpan<bool> inContext, string value)
    {
        var alone = SecretOutputRedactor.FindLogMasks(value);
        for (var i = 0; i < inContext.Length; i++)
        {
            if (inContext[i] && !alone[i])
                return true;
        }

        return false;
    }

    // Renders the template the way MEL and the Akka bridge do: "{{" and "}}" are braces, and a
    // placeholder "{Name}", "{Name,10}", or "{Name:x}" takes the value with that name. A repeated
    // name takes the next value with that name, or the last one when no more are left (Akka gives
    // one value for each name). A placeholder with no value stays as text.
    private static string Render(string template, KeyValuePair<string, object?>[] pairs, out List<ValueSpan> spans)
    {
        var byName = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i++)
        {
            if (pairs[i].Key == OriginalFormatKey)
                continue;
            if (!byName.TryGetValue(pairs[i].Key, out var indexes))
                byName[pairs[i].Key] = indexes = new Queue<int>();
            indexes.Enqueue(i);
        }

        spans = [];
        var text = new StringBuilder(template.Length + 64);
        var position = 0;
        while (position < template.Length)
        {
            var c = template[position];
            if (c is '{' or '}' && position + 1 < template.Length && template[position + 1] == c)
            {
                text.Append(c);
                position += 2;
                continue;
            }

            var close = c == '{' ? template.IndexOf('}', position + 1) : -1;
            if (close < 0)
            {
                text.Append(c);
                position++;
                continue;
            }

            var hole = template.AsSpan(position + 1, close - position - 1);
            var nameEnd = hole.IndexOfAny(',', ':');
            var name = (nameEnd < 0 ? hole : hole[..nameEnd]).Trim().ToString();
            if (byName.TryGetValue(name, out var queue))
            {
                var pairIndex = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                var value = RenderValue(pairs[pairIndex].Value);
                spans.Add(new ValueSpan(text.Length, value, pairIndex));
                text.Append(value);
            }
            else
            {
                text.Append(template, position, close - position + 1);
            }

            position = close + 1;
        }

        return text.ToString();
    }

    // The text that MEL writes for a value.
    private static string RenderValue(object? value)
        => value switch
        {
            null => "(null)",
            string text => text,
            IEnumerable items => string.Join(", ", items.Cast<object?>().Select(static item => item ?? "(null)")),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

    private readonly record struct ValueSpan(int Start, string Text, int PairIndex);
}

/// <summary>
/// The redacted copy of a record or scope. It keeps the key/value shape of MEL state, so a
/// provider that reads structured values (OTLP export, session routing) still works.
/// </summary>
internal sealed class RedactedLogState(string message, KeyValuePair<string, object?>[] values)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    public string Message { get; } = message;

    public int Count => values.Length;

    public KeyValuePair<string, object?> this[int index] => values[index];

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, object?>>)values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => Message;
}

/// <summary>
/// Stands in for an exception whose text needs escaping for a text log. Its message is the
/// normalized original message. Its text, which text sinks write, is the normalized original
/// text: the original type, message, and stack trace.
/// </summary>
internal sealed class NormalizedLogException(string message, string text) : Exception(message)
{
    public override string ToString() => text;
}
