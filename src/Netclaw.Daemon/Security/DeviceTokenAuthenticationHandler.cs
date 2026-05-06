// -----------------------------------------------------------------------
// <copyright file="DeviceTokenAuthenticationHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// ASP.NET Core authentication handler that validates device bearer tokens obtained
/// via the pairing flow (<c>POST /api/pair/exchange</c>).
///
/// <para>
/// On a match, the connection is granted <c>Operator</c> + <c>Verified</c> claims
/// with the device name as <c>SenderId</c>, and <c>LastUsedAt</c> is updated in the
/// <see cref="DeviceRegistry"/>.
/// </para>
///
/// <list type="bullet">
///   <item>Valid token → <see cref="AuthenticateResult.Success"/> with claims; updates <c>LastUsedAt</c>.</item>
///   <item>Token present but not matched → <see cref="AuthenticateResult.Fail"/>.</item>
///   <item>No <c>Authorization: Bearer</c> header → <see cref="AuthenticateResult.NoResult"/> (defers to other schemes).</item>
/// </list>
/// </summary>
internal sealed class DeviceTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DeviceBearer";

    private readonly DeviceRegistry _deviceRegistry;

    public DeviceTokenAuthenticationHandler(
        DeviceRegistry deviceRegistry,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _deviceRegistry = deviceRegistry;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return AuthenticateResult.NoResult();

        var headerValue = authHeader.FirstOrDefault();
        if (string.IsNullOrEmpty(headerValue) ||
            !headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = headerValue["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var device = await _deviceRegistry.LookupAndUpdateLastUsedAsync(token);
        if (device is null)
            return AuthenticateResult.Fail("Invalid or revoked device token.");

        var claims = new[]
        {
            new Claim(NetclawClaimTypes.PrincipalClassification,
                nameof(PrincipalClassification.Operator)),
            new Claim(NetclawClaimTypes.TransportAuthenticity,
                nameof(TransportAuthenticity.Verified)),
            new Claim(NetclawClaimTypes.DeviceId, device.Name),
            new Claim(NetclawClaimTypes.BootstrapDevice, device.IsBootstrapDevice.ToString()),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}
