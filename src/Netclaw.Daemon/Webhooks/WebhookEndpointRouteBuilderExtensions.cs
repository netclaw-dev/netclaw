// -----------------------------------------------------------------------
// <copyright file="WebhookEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

public static class WebhookEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var logger = app.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Netclaw.Daemon.Webhooks.Endpoint");

        app.MapPost("/api/webhooks/{route}", async ValueTask<Results<NotFound, StatusCodeHttpResult, BadRequest<WebhookErrorResponse>, UnauthorizedHttpResult, JsonHttpResult<WebhookIgnoredResponse>, JsonHttpResult<WebhookAcceptedResponse>>> (
            string route,
            HttpContext httpContext,
            WebhookRouteCatalog routeCatalog,
            WebhookRequestVerifier verifier,
            WebhookIngressGuard ingressGuard,
            IWebhookExecutionService executionService,
            IOperationalNotificationSink notificationSink,
            WebhooksConfig webhooksConfig,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            if (!webhooksConfig.Enabled)
                return TypedResults.NotFound();

            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();

            if (!routeCatalog.TryGetRoute(route, out var registeredRoute))
            {
                WebhookTelemetry.RecordRouteNotFound(route);
                logger.LogWarning(
                    "Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                    route, "route_not_found", remoteIp, (string?)null, (string?)null);
                return TypedResults.NotFound();
            }

            var bodyRead = await ReadRequestBodyAsync(httpContext.Request, registeredRoute.Config.MaxBodyBytes, ct);
            switch (bodyRead.Status)
            {
                case WebhookBodyReadStatus.TooLarge:
                    WebhookTelemetry.RecordBodyTooLarge(registeredRoute.Name);
                    logger.LogWarning(
                        "Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                        registeredRoute.Name, "body_too_large", remoteIp, (string?)null, (string?)null);
                    return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

                case WebhookBodyReadStatus.InvalidJson:
                    WebhookTelemetry.RecordInvalidJson(registeredRoute.Name);
                    logger.LogWarning(
                        "Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                        registeredRoute.Name, "invalid_json", remoteIp, (string?)null, (string?)null);
                    return TypedResults.BadRequest(new WebhookErrorResponse("Invalid JSON request body."));
            }

            var verification = verifier.Verify(registeredRoute, httpContext.Request.Headers, bodyRead.BodyBytes!);
            if (!verification.IsAccepted)
            {
                WebhookTelemetry.RecordVerificationFailed(registeredRoute.Name);
                logger.LogWarning(
                    "Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                    registeredRoute.Name, "verification_failed", remoteIp, verification.DeliveryId, verification.EventType);
                return TypedResults.Unauthorized();
            }

            if (!registeredRoute.IsEventAllowed(verification.EventType))
            {
                WebhookTelemetry.RecordEventFiltered(registeredRoute.Name);
                logger.LogDebug(
                    "Webhook filtered route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                    registeredRoute.Name, "event_filtered", remoteIp, verification.DeliveryId, verification.EventType);
                return TypedResults.Json(
                    new WebhookIgnoredResponse("ignored", "event_filtered"),
                    statusCode: StatusCodes.Status202Accepted);
            }

            var guardDecision = ingressGuard.CheckAndRecord(
                registeredRoute.Name,
                verification.DeliveryId,
                registeredRoute.Config.RateLimitPerMinute);

            if (guardDecision.Kind == WebhookIngressDecisionKind.Duplicate)
            {
                WebhookTelemetry.RecordDuplicateDelivery(registeredRoute.Name);
                logger.LogDebug(
                    "Webhook filtered route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                    registeredRoute.Name, "duplicate_delivery", remoteIp, verification.DeliveryId, verification.EventType);
                return TypedResults.Json(
                    new WebhookIgnoredResponse("ignored", "duplicate_delivery"),
                    statusCode: StatusCodes.Status202Accepted);
            }

            if (guardDecision.Kind == WebhookIngressDecisionKind.RateLimited)
            {
                WebhookTelemetry.RecordRateLimited(registeredRoute.Name);
                logger.LogWarning(
                    "Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                    registeredRoute.Name, "rate_limited", remoteIp, verification.DeliveryId, verification.EventType);

                if (guardDecision.RetryAfterSeconds is { } retryAfter)
                    httpContext.Response.Headers.RetryAfter = retryAfter.ToString();

                return TypedResults.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var now = timeProvider.GetUtcNow();
            var deliveryKey = string.IsNullOrWhiteSpace(verification.DeliveryId)
                ? $"{now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"
                : verification.DeliveryId!;

            var sessionId = new SessionId($"webhook/{registeredRoute.Name}/{SanitizeWebhookId(deliveryKey)}");
            var invocation = new WebhookInvocation(
                registeredRoute,
                verification.EventType is { } eventType ? new WebhookEventType(eventType) : null,
                verification.DeliveryId is { } deliveryId ? new WebhookDeliveryId(deliveryId) : null,
                bodyRead.BodyJson!,
                sessionId,
                now);

            WebhookTelemetry.RecordAccepted(registeredRoute.Name);
            logger.LogInformation(
                "Webhook accepted route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                registeredRoute.Name, "accepted", remoteIp, verification.DeliveryId, verification.EventType);

            notificationSink.Emit(OperationalAlert.Create(
                timeProvider,
                "webhook.received",
                AlertType.WebhookReceived,
                $"Webhook '{registeredRoute.Name}' received event '{verification.EventType ?? "unknown"}'",
                AlertSeverity.Info,
                source: registeredRoute.Name,
                context: new Dictionary<string, string>
                {
                    ["route"] = registeredRoute.Name,
                    ["event"] = verification.EventType ?? "unknown",
                    ["deliveryId"] = verification.DeliveryId ?? "generated",
                    ["sessionId"] = sessionId.Value,
                }));

            executionService.StartInvocation(invocation);
            return TypedResults.Json(
                new WebhookAcceptedResponse(
                    Status: "accepted",
                    Route: registeredRoute.Name,
                    EventType: verification.EventType,
                    DeliveryId: verification.DeliveryId,
                    SessionId: sessionId.Value),
                statusCode: StatusCodes.Status202Accepted);
        })
        .WithName("ReceiveWebhook")
        .WithSummary("Receive, verify, and dispatch an inbound webhook delivery.")
        .WithTags("Webhooks")
        .AllowAnonymous();

        return app;
    }

    internal static async Task<WebhookBodyReadResult> ReadRequestBodyAsync(HttpRequest request, int maxBodyBytes, CancellationToken ct)
    {
        if (request.ContentLength is > 0 and var contentLength && contentLength > maxBodyBytes)
            return WebhookBodyReadResult.TooLarge();

        var buffer = new byte[8192];
        await using var ms = new MemoryStream();
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;

            await ms.WriteAsync(buffer.AsMemory(0, read), ct);
            if (ms.Length > maxBodyBytes)
                return WebhookBodyReadResult.TooLarge();
        }

        var bodyBytes = ms.ToArray();
        var bodyJson = Encoding.UTF8.GetString(bodyBytes);

        try
        {
            using var _ = JsonDocument.Parse(bodyJson);
            return WebhookBodyReadResult.Ok(bodyBytes, bodyJson);
        }
        catch (JsonException)
        {
            return WebhookBodyReadResult.InvalidJson();
        }
    }

    internal static string SanitizeWebhookId(string value)
    {
        var sanitized = new string([.. value.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')]);

        return string.IsNullOrWhiteSpace(sanitized)
            ? Guid.NewGuid().ToString("N")
            : sanitized;
    }
}

/// <summary>Error payload returned when a webhook request is malformed.</summary>
internal sealed record WebhookErrorResponse(string Error);

/// <summary>Acknowledgement that a webhook delivery was accepted but not acted on.</summary>
internal sealed record WebhookIgnoredResponse(string Status, string Reason);

/// <summary>Acknowledgement that a webhook delivery was accepted and dispatched.</summary>
internal sealed record WebhookAcceptedResponse(
    string Status,
    string Route,
    string? EventType,
    string? DeliveryId,
    string SessionId);

internal enum WebhookBodyReadStatus
{
    Ok,
    TooLarge,
    InvalidJson
}

internal sealed record WebhookBodyReadResult(WebhookBodyReadStatus Status, byte[]? BodyBytes, string? BodyJson)
{
    public static WebhookBodyReadResult Ok(byte[] bodyBytes, string bodyJson)
        => new(WebhookBodyReadStatus.Ok, bodyBytes, bodyJson);
    public static WebhookBodyReadResult TooLarge() => new(WebhookBodyReadStatus.TooLarge, null, null);
    public static WebhookBodyReadResult InvalidJson() => new(WebhookBodyReadStatus.InvalidJson, null, null);
}
