// -----------------------------------------------------------------------
// <copyright file="IDaemonHubTransport.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.SignalR.Client;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// The transport seam under <see cref="DaemonClient"/>. It hides the concrete
/// SignalR <see cref="HubConnection"/> so the client's reconnect and session
/// state machine can run against a controllable fake in unit tests — no sockets,
/// no ports, no wall-clock timing.
/// </summary>
/// <remarks>
/// The seam exposes only what the owner loop needs. The owner is the single
/// caller of <see cref="StartAsync"/> and the single consumer of
/// <see cref="Closed"/>, so this transport carries no reconnect policy of its
/// own — the real adapter disables SignalR auto-reconnect on purpose.
/// </remarks>
internal interface IDaemonHubTransport : IAsyncDisposable
{
    /// <summary>True when the transport currently has a live connection.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Raised when the transport drops. The owner loop turns this into a
    /// reconnect. The handler runs on a transport callback thread, so it must
    /// only post to the owner and return.
    /// </summary>
    event Func<Exception?, Task>? Closed;

    /// <summary>Registers a server-to-client push handler (e.g. <c>ReceiveOutput</c>).</summary>
    IDisposable On<TMessage>(string methodName, Action<TMessage> handler);

    /// <summary>Starts the transport. Throws on failure; leaves the transport disconnected.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Invokes a hub method that returns a value.</summary>
    Task<TResult> InvokeAsync<TResult>(string methodName, object?[] args, CancellationToken cancellationToken);

    /// <summary>Invokes a hub method that returns no value.</summary>
    Task InvokeAsync(string methodName, object?[] args, CancellationToken cancellationToken);
}

/// <summary>
/// The production <see cref="IDaemonHubTransport"/>. It wraps a real SignalR
/// <see cref="HubConnection"/>.
/// </summary>
/// <remarks>
/// The connection is built WITHOUT <c>WithAutomaticReconnect</c>. Reconnect is
/// owned entirely by <see cref="DaemonClient"/>'s single owner loop. A second
/// reconnect authority inside SignalR would race the owner for the one
/// connection — the exact defect this design removes.
/// </remarks>
internal sealed class SignalRDaemonHubTransport : IDaemonHubTransport
{
    private readonly HubConnection _connection;

    private SignalRDaemonHubTransport(HubConnection connection)
    {
        _connection = connection;
        _connection.Closed += OnConnectionClosed;
    }

    internal static SignalRDaemonHubTransport FromConnection(HubConnection connection)
        => new(connection);

    public static SignalRDaemonHubTransport Create(
        string hubUrl,
        Func<Task<string?>>? accessTokenProvider,
        TimeSpan? serverTimeout)
    {
        var connection = new HubConnectionBuilder()
            .ConfigureAccessToken(hubUrl, accessTokenProvider)
            .Build();

        if (serverTimeout is { } timeout)
            _ = SetServerTimeout(connection, timeout);

        return new SignalRDaemonHubTransport(connection);
    }

    private static HubConnection SetServerTimeout(HubConnection connection, TimeSpan timeout)
    {
        connection.ServerTimeout = timeout;
        return connection;
    }

    public bool IsConnected => _connection.State is HubConnectionState.Connected;

    public event Func<Exception?, Task>? Closed;

    private Task OnConnectionClosed(Exception? error) => Closed?.Invoke(error) ?? Task.CompletedTask;

    public IDisposable On<TMessage>(string methodName, Action<TMessage> handler)
        => _connection.On(methodName, handler);

    public Task StartAsync(CancellationToken cancellationToken)
        => _connection.StartAsync(cancellationToken);

    public Task<TResult> InvokeAsync<TResult>(string methodName, object?[] args, CancellationToken cancellationToken)
        => _connection.InvokeCoreAsync<TResult>(methodName, args, cancellationToken);

    public Task InvokeAsync(string methodName, object?[] args, CancellationToken cancellationToken)
        => _connection.InvokeCoreAsync(methodName, args, cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
