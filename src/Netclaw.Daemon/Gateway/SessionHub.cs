// -----------------------------------------------------------------------
// <copyright file="SessionHub.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;

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
///   GeneratePairingCode() → PairingCodeResultDto (loopback Operator only)
///
/// Server → Client:
///   ReceiveOutput(output: SessionOutputDto) → void
/// </code>
/// </summary>
[Authorize]
public sealed class SessionHub : Hub<ISessionHubClient>
{
    private readonly SessionRegistry _registry;
    private readonly PairingCodeService _pairingCodeService;
    private readonly ILogger<SessionHub> _logger;

    public SessionHub(
        SessionRegistry registry,
        PairingCodeService pairingCodeService,
        ILogger<SessionHub> logger)
    {
        _registry = registry;
        _pairingCodeService = pairingCodeService;
        _logger = logger;
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

    /// <summary>
    /// Generates a device pairing code so a remote device can authenticate via
    /// <c>POST /api/pair/exchange</c>.
    ///
    /// <para>Restricted to loopback (<c>LocalProcess</c>) connections only — a remote
    /// device that already has a token cannot generate new codes. This enforces the
    /// trust chain: local operator approves pairing → remote device gets a token.</para>
    ///
    /// <para>The daemon logs the code at <c>Information</c> level so Docker operators
    /// can retrieve it from container logs without needing a CLI inside the container.</para>
    /// </summary>
    /// <exception cref="HubException">Thrown when the caller is not a loopback connection.</exception>
    public Task<PairingCodeResultDto> GeneratePairingCode()
    {
        var transport = Context.User?.FindFirst(NetclawClaimTypes.TransportAuthenticity)?.Value;
        if (transport != nameof(TransportAuthenticity.LocalProcess))
            throw new HubException("GeneratePairingCode requires a local loopback connection. Use `netclaw daemon pair` from the daemon host.");

        var (formattedCode, expiresAt) = _pairingCodeService.GenerateCode();

        // Log at Information so the code appears in stdout / container logs.
        // This lets Docker operators retrieve the code from `docker logs` without
        // needing a shell inside the container.
        _logger.LogInformation("Pairing code generated: {Code} (expires {ExpiresAt:o})", formattedCode, expiresAt);

        return Task.FromResult(new PairingCodeResultDto(formattedCode, expiresAt));
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _registry.OnDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
