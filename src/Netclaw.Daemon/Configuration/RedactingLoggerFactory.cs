// -----------------------------------------------------------------------
// <copyright file="RedactingLoggerFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
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
            redacted = new RedactedLogState(
                RedactText(formatter(state, exception)),
                RedactValues(state as IEnumerable<KeyValuePair<string, object?>>));
            redactedException = exception is null ? null : SecretOutputRedactor.RedactForLogging(exception);
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
                IEnumerable<KeyValuePair<string, object?>> pairs => new RedactedLogState(
                    RedactText(state.ToString() ?? string.Empty),
                    RedactValues(pairs)),
                _ => RedactText(state.ToString() ?? string.Empty),
            };
        }
        catch (Exception failure)
        {
            // Fail closed, and do not throw into the code that opened the scope.
            return $"[log scope withheld: redaction failed: {failure.GetType().Name}]";
        }
    }

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
