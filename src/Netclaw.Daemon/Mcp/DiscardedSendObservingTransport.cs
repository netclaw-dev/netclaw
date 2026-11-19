// -----------------------------------------------------------------------
// <copyright file="DiscardedSendObservingTransport.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Netclaw.Security;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Wraps an MCP client transport so that the session transport it connects is a
/// <see cref="DiscardedSendObservingTransport"/> (#2258).
/// </summary>
internal sealed class DiscardedSendObservingClientTransport(IClientTransport inner, ILogger logger)
    : IClientTransport
{
    public string Name => inner.Name;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var connected = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new DiscardedSendObservingTransport(connected, inner.Name, logger);
    }
}

/// <summary>
/// Observes the sends that the ModelContextProtocol SDK (2.2.0) starts and then discards.
/// <list type="bullet">
/// <item><c>McpSessionHandler.ProcessMessagesCoreAsync</c> handles each incoming message in a
/// fire-and-forget task. When a server-to-client request fails, the task sends a
/// <see cref="JsonRpcError"/> reply from its catch block.</item>
/// <item><c>McpSessionHandler.RegisterCancellation</c> sends <c>notifications/cancelled</c>
/// and discards the task.</item>
/// </list>
/// If the server returns an HTTP error (for example 502) or the connection drops, those
/// sends throw and no code observes the task. The finalizer then raises
/// <see cref="TaskScheduler.UnobservedTaskException"/>, and the daemon reported that as a crash.
/// This decorator logs one Warning line that names the server and does not rethrow for
/// those two message shapes. All other sends propagate unchanged, because a caller awaits them.
/// </summary>
/// <remarks>
/// The SDK checks the concrete transport type in one place only: <c>McpClientImpl</c> writes a
/// debug log on a tool cache miss when the transport is <c>StreamableHttpClientSessionTransport</c>.
/// The decorator suppresses only that debug log. Completion details flow through
/// <see cref="MessageReader"/>, which the decorator forwards.
/// </remarks>
internal sealed partial class DiscardedSendObservingTransport(ITransport inner, string serverName, ILogger logger)
    : ITransport
{
    private const int MaxDetailLength = 300;

    public string? SessionId => inner.SessionId;

    public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        => IsDiscardedBySdk(message)
            ? SendObservedAsync(message, cancellationToken)
            : inner.SendMessageAsync(message, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private static bool IsDiscardedBySdk(JsonRpcMessage message)
        => message is JsonRpcError
            or JsonRpcNotification { Method: NotificationMethods.CancelledNotification };

    private async Task SendObservedAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await inner.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The session shuts down. The server does not need this reply any more.
            logger.LogDebug(
                "MCP server '{Name}': session shut down before Netclaw sent {MessageKind}",
                serverName,
                DescribeMessage(message));
        }
        catch (Exception ex)
        {
            // Single line, no stack: the failure is the server's, and a 502 body is HTML.
            logger.LogWarning(
                "MCP server '{Name}': could not send {MessageKind}: {ExceptionType}: {Detail}",
                serverName,
                DescribeMessage(message),
                ex.GetType().Name,
                FormatDetail(ex.Message));
        }
    }

    private static string DescribeMessage(JsonRpcMessage message)
        => message is JsonRpcNotification notification
            ? notification.Method
            : "JSON-RPC error reply";

    private static string FormatDetail(string message)
    {
        var singleLine = WhitespaceRun().Replace(SecretOutputRedactor.Redact(message), " ").Trim();
        return singleLine.Length <= MaxDetailLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, MaxDetailLength), "...");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
