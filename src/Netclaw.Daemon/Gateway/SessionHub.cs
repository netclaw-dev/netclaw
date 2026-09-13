// -----------------------------------------------------------------------
// <copyright file="SessionHub.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

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
///   AttachSession(sessionId: string) → void
///   SendMessage(sessionId: string, text: string) → void
///   RespondToInteraction(sessionId: string, callId: string, selectedKey: string) → void
///
/// Server → Client:
///   ReceiveOutput(output: SessionOutputDto) → void
/// </code>
/// </summary>
[Authorize]
public sealed class SessionHub : Hub<ISessionHubClient>
{
    private readonly SessionRegistry _registry;

    public SessionHub(SessionRegistry registry)
    {
        _registry = registry;
    }

    public Task<string> CreateSession(string channelType)
    {
        return _registry.CreateSessionAsync(Context.ConnectionId, channelType, Context.User);
    }

    public Task<SessionEnsureResultDto> EnsureSession(string? sessionId, string channelType)
    {
        return _registry.EnsureSessionAsync(Context.ConnectionId, sessionId, channelType, Context.User);
    }

    public Task AttachSession(string sessionId)
    {
        return _registry.AttachSessionAsync(Context.ConnectionId, sessionId, Context.User);
    }

    public Task SendMessage(string sessionId, string text)
    {
        return _registry.SendMessageAsync(Context.ConnectionId, sessionId, text, Context.User);
    }

    public Task RespondToInteraction(string sessionId, string callId, string selectedKey)
    {
        return _registry.RespondToInteractionAsync(Context.ConnectionId, sessionId, callId, selectedKey, Context.User);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _registry.OnDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
