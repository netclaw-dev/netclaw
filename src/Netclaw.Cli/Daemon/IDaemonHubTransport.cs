// -----------------------------------------------------------------------
// <copyright file="IDaemonHubTransport.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.SignalR.Client;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using static Netclaw.Cli.Daemon.ChatClientProtocol;

namespace Netclaw.Cli.Daemon;

internal interface IDaemonHubTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    IDisposable Subscribe(Action<TransportEvent> notify);
    Task StartAsync(CancellationToken cancellationToken);
    Task<SessionBinding> EnsureSessionAsync(SessionId? sessionId, ChannelType channel, CancellationToken cancellationToken);
    Task SendAsync(SessionId sessionId, string text, CancellationToken cancellationToken);
    Task RespondAsync(SessionId sessionId, ToolCallId callId, ApprovalOptionKey selection, CancellationToken cancellationToken);
}

internal sealed class SignalRDaemonHubTransport(HubConnection connection) : IDaemonHubTransport
{
    internal static SignalRDaemonHubTransport FromConnection(HubConnection connection) => new(connection);

    public static SignalRDaemonHubTransport Create(
        string hubUrl, Func<Task<string?>>? accessTokenProvider, TimeSpan? serverTimeout)
    {
        var connection = new HubConnectionBuilder().ConfigureAccessToken(hubUrl, accessTokenProvider).Build();
        if (serverTimeout is { } timeout) connection.ServerTimeout = timeout;
        return new SignalRDaemonHubTransport(connection);
    }

    public bool IsConnected => connection.State is HubConnectionState.Connected;

    public IDisposable Subscribe(Action<TransportEvent> notify)
    {
        Task Closed(Exception? error) { notify(new TransportDropped(error)); return Task.CompletedTask; }
        var output = connection.On<SessionOutputDto>("ReceiveOutput", dto =>
            notify(new OutputReceived(SessionOutputDtoMapper.FromDto(dto))));
        connection.Closed += Closed;
        return new Subscription(connection, output, Closed);
    }

    public Task StartAsync(CancellationToken cancellationToken) => connection.StartAsync(cancellationToken);

    public async Task<SessionBinding> EnsureSessionAsync(SessionId? sessionId, ChannelType channel, CancellationToken cancellationToken)
    {
        var result = await connection.InvokeCoreAsync<SessionEnsureResultDto>(
            "EnsureSession", [sessionId?.Value, channel.ToWireValue()], cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.SessionId);
        return new SessionBinding(new SessionId(result.SessionId), result.TextAdmissionVersion);
    }

    public Task SendAsync(SessionId sessionId, string text, CancellationToken cancellationToken)
        => connection.InvokeCoreAsync("SendMessage", [sessionId.Value, text], cancellationToken);

    public Task RespondAsync(SessionId sessionId, ToolCallId callId, ApprovalOptionKey selection, CancellationToken cancellationToken)
        => connection.InvokeCoreAsync("RespondToInteraction", [sessionId.Value, callId.Value, selection.Value], cancellationToken);

    public ValueTask DisposeAsync() => connection.DisposeAsync();

    private sealed class Subscription(HubConnection connection, IDisposable output, Func<Exception?, Task> closed) : IDisposable
    {
        public void Dispose() { connection.Closed -= closed; output.Dispose(); }
    }
}
