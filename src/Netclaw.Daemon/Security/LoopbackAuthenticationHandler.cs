// -----------------------------------------------------------------------
// <copyright file="LoopbackAuthenticationHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// ASP.NET Core authentication handler that trusts connections originating from the
/// loopback interface (<c>127.0.0.0/8</c> / <c>::1</c>).
///
/// Loopback connections are granted <c>Operator</c> + <c>LocalProcess</c> identity
/// because only a process running on the same machine can reach the loopback address.
/// Non-loopback connections receive <see cref="AuthenticateResult.NoResult"/> so that
/// other schemes (e.g. device bearer token, future OAuth) can take over.
///
/// A loopback source address does not prove that the operator made the request: a web
/// page in the operator's browser can reach the daemon by DNS rebinding or by a cross-site
/// request. Both carry a foreign <c>Host</c> or <c>Origin</c> header, so
/// <see cref="LoopbackHostOriginPolicy"/> refuses them.
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

        var request = Context.Request;
        var refusal = LoopbackHostOriginPolicy.FindRefusal(request.Host, request.Headers.Origin);
        if (refusal is not null)
            return Task.FromResult(AuthenticateResult.Fail(refusal));

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

    // Anonymous routes run this scheme too and answer 200, so only a real denial is logged:
    // a web page must not fill the log through a route that needs no authentication.
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync();
        if (result.Failure is not null)
        {
            var request = Context.Request;
            Logger.LogWarning(
                "Refused loopback request: {Reason}. Host={Host} Origin={Origin}",
                result.Failure.Message,
                request.Host.Value,
                request.Headers.Origin.ToString());
        }

        await base.HandleChallengeAsync(properties);
    }

    private static bool IsLoopback(IPAddress address)
        => IPAddress.IsLoopback(address)
           || address.Equals(IPAddress.IPv6Loopback);
}

/// <summary>
/// The fixed Host and Origin rule for loopback operator trust. The Host name must pass
/// <see cref="LoopbackHostName.IsStrictLoopback"/>: <c>localhost</c> or the canonical text of a
/// loopback IP literal. The rule does not use the bind validator, so a wider bind value
/// cannot widen request admission. A <c>Daemon.Host</c> value reaches the daemon through the
/// CLI <c>Uri</c>, which rewrites a short form to canonical text, and the Host text must be
/// canonical. A browser always sends a <c>Host</c> equal to the name in its address bar and an
/// <c>Origin</c> that names the page that made a cross-site request. The daemon serves plain
/// HTTP in Local mode, so the only valid Origin is <c>http://</c> plus the request Host.
/// </summary>
internal static class LoopbackHostOriginPolicy
{
    public static string? FindRefusal(HostString host, StringValues origin)
    {
        if (!IsLoopbackHostName(host))
            return "Host is not a loopback name";

        if (origin.Count == 0)
            return null;

        var expected = "http://" + host.Value;
        if (origin.Count != 1 || !string.Equals(origin[0], expected, StringComparison.OrdinalIgnoreCase))
            return "Origin does not match Host";

        return null;
    }

    private static bool IsLoopbackHostName(HostString host)
    {
        if (!host.HasValue)
            return false;

        var value = host.Value;
        var name = host.Host;
        if (!value.StartsWith(name, StringComparison.Ordinal) || !IsEmptyOrPortSuffix(value.AsSpan(name.Length)))
            return false;

        // An IPv6 literal must have brackets in a Host header, and a name or IPv4 literal must not.
        var bracketed = name.StartsWith('[') && name.EndsWith(']');
        var text = bracketed ? name[1..^1] : name;
        return text.Contains(':', StringComparison.Ordinal) == bracketed && LoopbackHostName.IsStrictLoopback(text);
    }

    private static bool IsEmptyOrPortSuffix(ReadOnlySpan<char> suffix)
    {
        if (suffix.IsEmpty)
            return true;

        if (suffix[0] != ':' || suffix.Length == 1 || suffix.Length > 6)
            return false;

        foreach (var c in suffix[1..])
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }
}
