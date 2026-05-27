// -----------------------------------------------------------------------
// <copyright file="SessionHub.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Sockets;
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
    private readonly DaemonConfig _daemonConfig;
    private readonly ILogger<SessionHub> _logger;

    public SessionHub(
        SessionRegistry registry,
        PairingCodeService pairingCodeService,
        DaemonConfig daemonConfig,
        ILogger<SessionHub> logger)
    {
        _registry = registry;
        _pairingCodeService = pairingCodeService;
        _daemonConfig = daemonConfig;
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
    /// <para>Restricted to daemon-host operator trust: either a loopback-authenticated local
    /// process or a direct authenticated control-plane connection from the daemon host.
    /// Remote paired devices cannot mint new codes, even when their traffic arrives
    /// through a trusted reverse proxy.</para>
    ///
    /// <para>The daemon logs the code at <c>Information</c> level so Docker operators
    /// can retrieve it from container logs without needing a CLI inside the container.</para>
    /// </summary>
    /// <exception cref="HubException">Thrown when the caller is not a daemon-host operator.</exception>
    public Task<PairingCodeResultDto> GeneratePairingCode()
    {
        var remoteIp = NormalizeIp(Context.GetHttpContext()?.Connection.RemoteIpAddress);
        var localIp = NormalizeIp(Context.GetHttpContext()?.Connection.LocalIpAddress);
        var transport = Context.User?.FindFirst(NetclawClaimTypes.TransportAuthenticity)?.Value;
        var isDirectLocalControlPlaneConnection = IsDirectLocalControlPlaneConnection(
            remoteIp, localIp, _daemonConfig.ExposureMode);

        if (transport != nameof(TransportAuthenticity.LocalProcess)
            && (transport != nameof(TransportAuthenticity.Verified) || !isDirectLocalControlPlaneConnection))
        {
            throw new HubException(
                "GeneratePairingCode requires a daemon-host local operator connection or direct authenticated local control-plane access.");
        }

        var (formattedCode, expiresAt) = _pairingCodeService.GenerateCode();

        // Log at Information so the code appears in stdout / container logs.
        // This lets Docker operators retrieve the code from `docker logs` without
        // needing a shell inside the container.
        _logger.LogInformation("Pairing code generated: {Code} (expires {ExpiresAt:o})", formattedCode, expiresAt);

        return Task.FromResult(new PairingCodeResultDto(formattedCode, expiresAt));
    }

    internal static bool IsDirectLocalControlPlaneConnection(
        IPAddress remoteIp,
        IPAddress localIp,
        ExposureMode exposureMode)
    {
        // Under any exposure mode that accepts remote traffic (reverse proxy or
        // any tunnel agent), a loopback RemoteIpAddress is the local forwarder,
        // NOT a same-host operator process. Only ExposureMode.Local can treat
        // loopback as proof of a direct local control-plane caller — mirrors
        // the LoopbackAuthenticationHandler trust rule and prevents an
        // authenticated remote device from minting additional pairing codes via
        // a tunnel (audit finding #24 follow-up).
        if (IPAddress.IsLoopback(remoteIp))
            return exposureMode == ExposureMode.Local;

        // Non-loopback equality with the local interface address still indicates
        // a direct on-host connection (e.g., a process binding to the daemon's
        // public address from the same machine). This path is unchanged and
        // governed separately by UseForwardedHeaders being wired only for
        // ReverseProxy mode.
        return localIp != IPAddress.None && remoteIp.Equals(localIp);
    }

    private static IPAddress NormalizeIp(IPAddress? address)
    {
        if (address is null)
            return IPAddress.None;

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _registry.OnDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
