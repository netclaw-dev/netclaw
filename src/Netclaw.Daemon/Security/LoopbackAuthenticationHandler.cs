// -----------------------------------------------------------------------
// <copyright file="LoopbackAuthenticationHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// ASP.NET Core authentication handler that trusts connections originating from the
/// loopback interface (<c>127.0.0.1</c> / <c>::1</c>).
///
/// Loopback connections are granted <c>Operator</c> + <c>LocalProcess</c> identity
/// because only a process running on the same machine can reach the loopback address.
/// Non-loopback connections receive <see cref="AuthenticateResult.NoResult"/> so that
/// other schemes (e.g. device bearer token, future OAuth) can take over.
/// </summary>
public sealed class LoopbackAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Loopback";
    private readonly DaemonConfig _daemonConfig;

    public LoopbackAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        DaemonConfig daemonConfig)
        : base(options, logger, encoder)
    {
        _daemonConfig = daemonConfig;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Any exposure mode that accepts remote traffic must never inherit
        // loopback operator trust. Tunnel agents (tailscaled, cloudflared) and
        // reverse proxies all forward remote connections over the loopback
        // socket, so a loopback source address is NOT proof of a same-host
        // caller in those modes. Remote traffic must flow through a
        // remote-authenticated scheme (the device bearer token) instead — fail
        // closed here. Only ExposureMode.Local treats loopback as truly local.
        if (_daemonConfig.ExposureMode.RequiresRemoteAuthentication())
            return Task.FromResult(AuthenticateResult.NoResult());

        var remoteIp = Context.Connection.RemoteIpAddress;

        if (remoteIp is null || !IsLoopback(remoteIp))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new[]
        {
            new Claim(NetclawClaimTypes.PrincipalClassification,
                nameof(PrincipalClassification.Operator)),
            new Claim(NetclawClaimTypes.TransportAuthenticity,
                nameof(TransportAuthenticity.LocalProcess)),
            new Claim(NetclawClaimTypes.DeviceId, "local"),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool IsLoopback(IPAddress address)
        => IPAddress.IsLoopback(address)
           || address.Equals(IPAddress.IPv6Loopback);
}
