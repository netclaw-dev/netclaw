// -----------------------------------------------------------------------
// <copyright file="PairingEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

public static class PairingEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        // Device pairing exchange — unauthenticated, rate-limited, with per-IP lockout guard.
        // Accepts a time-limited pairing code and a device name; returns a bearer token on success.
        app.MapPost("/api/pair/exchange", async ValueTask<Results<Ok<PairingTokenResponse>, BadRequest<PairingErrorResponse>, NotFound, Conflict<PairingErrorResponse>, JsonHttpResult<PairingErrorResponse>>> (
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
                return TypedResults.Json(
                    new PairingErrorResponse("Too many failed attempts. Try again later."),
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            // Layer 2: No-code-pending gate — if no code exists, hide the endpoint entirely.
            if (pairingCodeService.GetPendingExpiry() is null)
                return TypedResults.NotFound();

            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.DeviceName))
                return TypedResults.BadRequest(new PairingErrorResponse("code and deviceName are required."));

            if (!pairingCodeService.TryConsume(request.Code))
            {
                exchangeGuard.RecordFailure(remoteIp);
                return TypedResults.Json(
                    new PairingErrorResponse("Invalid, expired, or already-used pairing code."),
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
                return TypedResults.Conflict(new PairingErrorResponse(ex.Message));
            }

            return TypedResults.Ok(new PairingTokenResponse(rawToken));
        })
        .WithName("ExchangePairingCode")
        .WithSummary("Exchange a pairing code for a device bearer token.")
        .WithTags("Pairing")
        .RequireRateLimiting("pairing-exchange").AllowAnonymous();

        // Device registry management — authenticated (loopback or valid bearer token required).
        // Returns a sanitized view of paired devices (no TokenHash/Salt).
        app.MapGet("/api/pair/devices", async ValueTask<Ok<IEnumerable<PairedDeviceInfoDto>>> (DeviceRegistry deviceRegistry, CancellationToken ct) =>
        {
            var devices = await deviceRegistry.ListAsync(ct);
            var sanitized = devices.Select(d => new PairedDeviceInfoDto(d.Name, d.CreatedAt, d.LastUsedAt));
            return TypedResults.Ok(sanitized);
        })
        .WithName("ListPairedDevices")
        .WithSummary("List paired devices (token material excluded).")
        .WithTags("Pairing")
        .RequireAuthorization();

        app.MapDelete("/api/pair/devices/{name}", async ValueTask<Results<NoContent, NotFound<PairingErrorResponse>>> (string name, DeviceRegistry deviceRegistry, CancellationToken ct) =>
        {
            var removed = await deviceRegistry.RemoveAsync(name, ct);
            return removed
                ? TypedResults.NoContent()
                : TypedResults.NotFound(new PairingErrorResponse($"Device '{name}' not found."));
        })
        .WithName("RemovePairedDevice")
        .WithSummary("Remove a paired device by name.")
        .WithTags("Pairing")
        .RequireAuthorization();

        return app;
    }
}

/// <summary>
/// Request body for <c>POST /api/pair/exchange</c>.
/// </summary>
internal sealed record PairingCodeExchangeRequest(string Code, string DeviceName);

/// <summary>Bearer token issued on a successful pairing exchange.</summary>
internal sealed record PairingTokenResponse(string Token);

/// <summary>Error payload returned when a pairing request fails.</summary>
internal sealed record PairingErrorResponse(string Error);
