// -----------------------------------------------------------------------
// <copyright file="RedactingLoggerFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Logging;
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
    // MEL's key for the message template. The template is not part of the formatted message.
    private const string OriginalFormatKey = "{OriginalFormat}";

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
        var masked = SecretOutputRedactor.Redact(message);
        var messageLostText = !string.Equals(masked, message, StringComparison.Ordinal);
        KeyValuePair<string, object?>[] values = pairs is null
            ? []
            : [.. pairs.Select(pair => new KeyValuePair<string, object?>(
                pair.Key,
                RedactValue(pair, messageLostText ? (message, masked) : null)))];

        return new RedactedLogState(LogTextFraming.Normalize(masked), values);
    }

    // The template can hold the words that mark a secret, so that the value alone matches no
    // pattern: "Authorization: Bearer {Token}". When redaction removed the value's text from the
    // message, the value is masked too. The template is not a value in the message, so it is
    // only redacted as text.
    private static object? RedactValue(KeyValuePair<string, object?> pair, (string Original, string Masked)? message)
    {
        var value = pair.Value;
        if (value is null)
            return null;

        // Numbers, enums, and other convertible primitives cannot carry a secret on their own,
        // and they keep their type for structured sinks. They are rendered only when the
        // message lost text, so the usual record does not pay for it.
        if (value is IConvertible and not string)
        {
            return message is { } lost && WasMasked(pair.Key, Convert.ToString(value, CultureInfo.InvariantCulture), lost)
                ? SecretOutputRedactor.Redacted
                : value;
        }

        // Rendered the way MEL renders a value into the message.
        var rendered = value switch
        {
            string text => text,
            IEnumerable items => string.Join(", ", items.Cast<object?>()),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        // A value that holds its own secret marker keeps its partial mask.
        var redacted = SecretOutputRedactor.Redact(rendered);
        if (!string.Equals(redacted, rendered, StringComparison.Ordinal))
            return LogTextFraming.Normalize(redacted);

        return message is { } changed && WasMasked(pair.Key, rendered, changed)
            ? SecretOutputRedactor.Redacted
            : LogTextFraming.Normalize(rendered);
    }

    private static bool WasMasked(string key, string? rendered, (string Original, string Masked) message)
        => key != OriginalFormatKey
            && !string.IsNullOrEmpty(rendered)
            && message.Original.Contains(rendered, StringComparison.Ordinal)
            && !message.Masked.Contains(rendered, StringComparison.Ordinal);

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
            : new NormalizedLogException(normalized);
    }

    // Redact first: some secret patterns end at a line break, so they must see the original breaks.
    private static string RedactText(string text) => LogTextFraming.Normalize(SecretOutputRedactor.Redact(text));
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
/// Stands in for an exception whose text needs escaping for a text log. It carries the
/// original type, message, and stack trace as normalized text.
/// </summary>
internal sealed class NormalizedLogException(string text) : Exception(text)
{
    public override string ToString() => Message;
}
