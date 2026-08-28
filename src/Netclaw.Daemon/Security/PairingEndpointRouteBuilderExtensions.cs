// -----------------------------------------------------------------------
// <copyright file="PairingEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

public static class PairingEndpointRouteBuilderExtensions
{
    private const int MaximumLocalControlRequestBytes = 4 * 1024;

    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/local-control/v1/pairing-code", GeneratePairingCodeAsync)
            .WithName("GenerateHostPairingCodeV1")
            .WithSummary("Generate a pairing code with a daemon-host local-control proof.")
            .WithTags("Pairing")
            .AllowAnonymous();

        // Device pairing exchange — unauthenticated, rate-limited, with per-IP lockout guard.
        // Accepts a time-limited pairing code and a device name; returns a bearer token on success.
        app.MapPost("/api/pair/exchange", async ValueTask<Results<Ok<PairingTokenResponse>, BadRequest<PairingErrorResponse>, NotFound, Conflict<PairingErrorResponse>, JsonHttpResult<PairingErrorResponse>>> (
            HttpContext httpContext,
            PairingCodeExchangeRequest request,
            PairingExchangeGuard exchangeGuard,
            PairingCoordinator pairingCoordinator,
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

            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.DeviceName))
                return TypedResults.BadRequest(new PairingErrorResponse("code and deviceName are required."));

            var result = await pairingCoordinator.ExchangeAsync(request.Code, request.DeviceName, ct);
            if (result.Status is PairingExchangeStatus.NoCode)
                return TypedResults.NotFound();

            if (result.Status is PairingExchangeStatus.InvalidCode)
            {
                exchangeGuard.RecordFailure(remoteIp);
                return TypedResults.Json(
                    new PairingErrorResponse("Invalid, expired, or already-used pairing code."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (result.Status is PairingExchangeStatus.DuplicateName)
                return TypedResults.Conflict(new PairingErrorResponse(result.Error!));

            return TypedResults.Ok(new PairingTokenResponse(result.Token!));
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

    private static async Task<IResult> GeneratePairingCodeAsync(
        HttpContext httpContext,
        LocalControlPairingProofValidator proofValidator,
        PairingCoordinator pairingCoordinator,
        CancellationToken cancellationToken)
    {
        if (httpContext.Request.ContentLength > MaximumLocalControlRequestBytes)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var body = new byte[MaximumLocalControlRequestBytes + 1];
        var totalRead = 0;
        while (totalRead < body.Length)
        {
            var read = await httpContext.Request.Body.ReadAsync(body.AsMemory(totalRead), cancellationToken);
            if (read == 0)
                break;

            totalRead += read;
        }

        if (totalRead > MaximumLocalControlRequestBytes)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

        LocalControlPairingCodeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(
                body.AsSpan(0, totalRead),
                PairingEndpointJsonContext.Default.LocalControlPairingCodeRequest);
        }
        catch (JsonException)
        {
            return TypedResults.BadRequest(new PairingErrorResponse("The request body is invalid."));
        }

        if (request is null)
            return TypedResults.BadRequest(new PairingErrorResponse("The request body is invalid."));

        var validation = proofValidator.ValidateAndConsume(request.Proof);
        switch (validation)
        {
            case LocalControlPairingProofValidation.Valid:
                var result = await pairingCoordinator.GenerateCodeAsync(cancellationToken);
                return TypedResults.Ok(result);

            case LocalControlPairingProofValidation.UnsupportedVersion:
                return TypedResults.BadRequest(new PairingErrorResponse("unsupported_protocol_version"));

            case LocalControlPairingProofValidation.CapacityExhausted:
                return TypedResults.Json(
                    new PairingErrorResponse("Local control is temporarily unavailable."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            default:
                return TypedResults.Json(
                    new PairingErrorResponse("Invalid local-control proof."),
                    statusCode: StatusCodes.Status401Unauthorized);
        }
    }
}

/// <summary>
/// Request body for <c>POST /api/pair/exchange</c>.
/// </summary>
internal sealed record PairingCodeExchangeRequest(string Code, string DeviceName);

internal sealed record LocalControlPairingCodeRequest(string Proof);

/// <summary>Bearer token issued on a successful pairing exchange.</summary>
internal sealed record PairingTokenResponse(string Token);

/// <summary>Error payload returned when a pairing request fails.</summary>
internal sealed record PairingErrorResponse(string Error);

[JsonSerializable(typeof(LocalControlPairingCodeRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class PairingEndpointJsonContext : JsonSerializerContext;
