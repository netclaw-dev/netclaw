// -----------------------------------------------------------------------
// <copyright file="PairingEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

public static class PairingEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        // Device pairing exchange — unauthenticated, rate-limited, with per-IP lockout guard.
        // Accepts a time-limited pairing code and a device name; returns a bearer token on success.
        app.MapPost("/api/pair/exchange", async (
            HttpContext httpContext,
            PairingCodeExchangeRequest request,
            PairingCodeService pairingCodeService,
            PairingExchangeGuard exchangeGuard,
            DeviceRegistry deviceRegistry,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var remoteIp = httpContext.Connection.RemoteIpAddress;

            // Layer 1: Per-IP failure lockout — blocked IPs get 429 before any processing.
            if (exchangeGuard.IsBlocked(remoteIp))
            {
                var retryAfter = exchangeGuard.GetRetryAfterSeconds(remoteIp);
                httpContext.Response.Headers.RetryAfter = retryAfter?.ToString() ?? "900";
                return Results.Json(
                    new { error = "Too many failed attempts. Try again later." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            // Layer 2: No-code-pending gate — if no code exists, hide the endpoint entirely.
            if (pairingCodeService.GetPendingExpiry() is null)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.DeviceName))
                return Results.BadRequest(new { error = "code and deviceName are required." });

            if (!pairingCodeService.TryConsume(request.Code))
            {
                exchangeGuard.RecordFailure(remoteIp);
                return Results.Json(
                    new { error = "Invalid, expired, or already-used pairing code." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var rawToken = Base64Url.EncodeToString(tokenBytes);

            var saltBytes = RandomNumberGenerator.GetBytes(16);
            var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
            var tokenHash = PairedDevice.ComputeTokenHash(rawToken, saltHex);

            var now = timeProvider.GetUtcNow();
            var device = new PairedDevice
            {
                Name = request.DeviceName.Trim(),
                TokenHash = tokenHash,
                Salt = saltHex,
                CreatedAt = now,
                LastUsedAt = now,
            };

            try
            {
                await deviceRegistry.AddAsync(device, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            return Results.Ok(new { token = rawToken });
        }).RequireRateLimiting("pairing-exchange").AllowAnonymous();

        // Device registry management — authenticated (loopback or valid bearer token required).
        // Returns a sanitized view of paired devices (no TokenHash/Salt).
        app.MapGet("/api/pair/devices", async (DeviceRegistry deviceRegistry, CancellationToken ct) =>
        {
            var devices = await deviceRegistry.ListAsync(ct);
            var sanitized = devices.Select(d => new PairedDeviceInfoDto(d.Name, d.CreatedAt, d.LastUsedAt));
            return Results.Ok(sanitized);
        }).RequireAuthorization();

        app.MapDelete("/api/pair/devices/{name}", async (string name, DeviceRegistry deviceRegistry, CancellationToken ct) =>
        {
            var removed = await deviceRegistry.RemoveAsync(name, ct);
            return removed
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Device '{name}' not found." });
        }).RequireAuthorization();

        return app;
    }
}

/// <summary>
/// Request body for <c>POST /api/pair/exchange</c>.
/// </summary>
internal sealed record PairingCodeExchangeRequest(string Code, string DeviceName);
