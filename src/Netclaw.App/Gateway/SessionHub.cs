using Microsoft.AspNetCore.SignalR;

namespace Netclaw.App.Gateway;

/// <summary>
/// SignalR hub for remote session access. Bridges remote clients
/// (future Blazor ops console, remote CLI) to <see cref="Netclaw.Actors.Channels.SessionPipeline"/>.
///
/// Phase 1: mapped at <c>/hub/session</c> but not actively used by TUI or headless
/// modes (they use <c>SessionPipeline</c> directly, in-process).
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
public sealed class SessionHub : Hub
{
    // Phase 1 stub — hub is mapped but not wired to SessionPipeline.
    // Implementation deferred until remote clients (Blazor ops console) need it.

    public Task<string> CreateSession(string channelType)
    {
        // TODO: wire to SessionPipeline.CreateAsync() when remote clients are implemented
        throw new HubException("Remote sessions are not yet implemented. Use netclaw chat for local sessions.");
    }

    public Task SendMessage(string sessionId, string text)
    {
        // TODO: wire to materialized session input queue
        throw new HubException("Remote sessions are not yet implemented. Use netclaw chat for local sessions.");
    }
}
