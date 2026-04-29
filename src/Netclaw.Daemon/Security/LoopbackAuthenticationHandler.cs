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

    public LoopbackAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
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
