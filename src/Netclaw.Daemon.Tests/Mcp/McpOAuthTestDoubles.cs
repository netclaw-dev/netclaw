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
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public Exception? LastException { get; private set; }

    public List<Exception> Exceptions { get; } = [];

    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(formatter(state, exception));
        if (exception is not null)
        {
            LastException = exception;
            Exceptions.Add(exception);
        }
    }
}
