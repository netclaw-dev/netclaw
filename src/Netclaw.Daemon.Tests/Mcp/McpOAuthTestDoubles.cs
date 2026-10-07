// -----------------------------------------------------------------------
// <copyright file="McpOAuthTestDoubles.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Daemon.Mcp;

namespace Netclaw.Daemon.Tests.Mcp;

internal static class McpOAuthTestDoubles
{
    /// <summary>
    /// A registrar for tests that never take the explicit-authorization path. Any HTTP
    /// call fails loudly, so a test that starts registering by accident reports it
    /// instead of quietly behaving as if the server had no OAuth metadata.
    /// </summary>
    public static McpOAuthClientRegistrar UnusedRegistrar()
        => new(new HttpClient(new UnreachableHandler()), NullLogger<McpOAuthClientRegistrar>.Instance);

    public static McpOAuthClientRegistrar RegistrarFor(HttpClient httpClient)
        => new(httpClient, NullLogger<McpOAuthClientRegistrar>.Instance);

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                $"This test's MCP OAuth registrar was not expected to issue requests (attempted {request.RequestUri}).");
    }
}

/// <summary>
/// Captures both the exceptions and the rendered messages a component logs, so a test can
/// assert on diagnostics that never surface through a return value.
/// </summary>
/// <remarks>
/// A component can log from a background task while the test reads. A lock guards all
/// state, and each read returns a snapshot copy.
/// </remarks>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<Exception> _exceptions = [];
    private readonly List<string> _entries = [];
    private Exception? _lastException;

    public Exception? LastException
    {
        get
        {
            lock (_gate)
                return _lastException;
        }
    }

    public IReadOnlyList<Exception> Exceptions
    {
        get
        {
            lock (_gate)
                return [.. _exceptions];
        }
    }

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
                return [.. _entries];
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var entry = formatter(state, exception);
        lock (_gate)
        {
            _entries.Add(entry);
            if (exception is not null)
            {
                _lastException = exception;
                _exceptions.Add(exception);
            }
        }
    }
}
