using Microsoft.AspNetCore.SignalR;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// SignalR hub for remote session access. Bridges remote clients
/// (CLI thin client, future Blazor ops console) to <see cref="Netclaw.Actors.Channels.SessionPipeline"/>
/// via <see cref="SessionRegistry"/>.
///
/// Hub instances are transient (one per method invocation) — all session
/// state lives in <see cref="SessionRegistry"/> (singleton).
///
/// Contract:
/// <code>
/// Client → Server:
///   CreateSession(channelType: string) → sessionId: string
///   SendMessage(sessionId: string, text: string) → void
///
/// Server → Client:
///   ReceiveOutput(output: SessionOutputDto) → void
/// </code>
/// </summary>
public sealed class SessionHub : Hub<ISessionHubClient>
{
    private readonly SessionRegistry _registry;

    public SessionHub(SessionRegistry registry)
    {
        _registry = registry;
    }

    public Task<string> CreateSession(string channelType)
    {
        return _registry.CreateSessionAsync(Context.ConnectionId, channelType);
    }

    public Task SendMessage(string sessionId, string text)
    {
        return _registry.SendMessageAsync(sessionId, text);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        return _registry.OnDisconnectedAsync(Context.ConnectionId);
    }
}
