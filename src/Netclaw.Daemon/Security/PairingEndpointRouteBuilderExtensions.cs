// -----------------------------------------------------------------------
// <copyright file="PairingEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Daemon.Security;

public static class PairingEndpointRouteBuilderExtensions
{
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
            IRequiredActor<PairingActor> pairingActor,
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

            var actor = await pairingActor.GetAsync(ct);
            var result = await actor.Ask<PairingExchangeResult>(
                new PairingActor.ExchangeCode(request.Code, request.DeviceName, ct),
                Timeout.InfiniteTimeSpan,
                ct);
            switch (result.Status)
            {
                case PairingExchangeStatus.Success when result.Token is { } token:
                    return TypedResults.Ok(new PairingTokenResponse(token));

                case PairingExchangeStatus.NoCode:
                    return TypedResults.NotFound();

                case PairingExchangeStatus.InvalidCode:
                    exchangeGuard.RecordFailure(remoteIp);
                    return TypedResults.Json(
                        new PairingErrorResponse("Invalid, expired, or already-used pairing code."),
                        statusCode: StatusCodes.Status401Unauthorized);

                case PairingExchangeStatus.DuplicateName when result.Error is { } error:
                    return TypedResults.Conflict(new PairingErrorResponse(error));

                default:
                    throw new InvalidOperationException("The pairing exchange returned an invalid result state.");
            }
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
        LocalControlRequests localControl,
        IRequiredActor<PairingActor> pairingActor,
        CancellationToken cancellationToken)
    {
        var (_, refusal) = await localControl.AuthorizeAsync(
            httpContext,
            PairingEndpointJsonContext.Default.LocalControlPairingCodeRequest,
            cancellationToken);
        if (refusal is not null)
            return refusal;

        var actor = await pairingActor.GetAsync(cancellationToken);
        var result = await actor.Ask<PairingCodeResultDto>(
            new PairingActor.GenerateCode(cancellationToken),
            Timeout.InfiniteTimeSpan,
            cancellationToken);
        return TypedResults.Ok(result);
    }
}

/// <summary>
/// Request body for <c>POST /api/pair/exchange</c>.
/// </summary>
internal sealed record PairingCodeExchangeRequest(string Code, string DeviceName);

internal sealed record LocalControlPairingCodeRequest(string Proof) : ILocalControlRequest
{
    [JsonIgnore]
    public LocalControlOperation? Operation => LocalControlOperation.GeneratePairingCode;
}

/// <summary>
/// Request body for <c>POST /api/local-control/v1/provider-oauth-result</c>. The proof
/// is bound to <see cref="State"/>.
/// </summary>
internal sealed record LocalControlProviderOAuthResultRequest(string State, string Proof) : ILocalControlRequest
{
    [JsonIgnore]
    public LocalControlOperation? Operation => string.IsNullOrEmpty(State)
        ? null
        : LocalControlOperation.ReadProviderOAuthResult(State);
}

/// <summary>Bearer token issued on a successful pairing exchange.</summary>
internal sealed record PairingTokenResponse(string Token);

/// <summary>Error payload returned when a pairing request fails.</summary>
internal sealed record PairingErrorResponse(string Error);

[JsonSerializable(typeof(LocalControlPairingCodeRequest))]
[JsonSerializable(typeof(LocalControlProviderOAuthResultRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class PairingEndpointJsonContext : JsonSerializerContext;
