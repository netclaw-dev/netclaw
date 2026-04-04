using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

        app.MapPost("/api/webhooks/{route}", async (
            string route,
            HttpContext httpContext,
            WebhookRouteCatalog routeCatalog,
            WebhookRequestVerifier verifier,
            WebhookIngressGuard ingressGuard,
            IWebhookExecutionService executionService,
            IOperationalNotificationSink notificationSink,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();

            if (!routeCatalog.TryGetRoute(route, out var registeredRoute))
            {
                WebhookTelemetry.RecordRouteNotFound(route);
                logger.LogWarning(
                    "Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                    route, "route_not_found", remoteIp, (string?)null, (string?)null);
                return Results.NotFound();
            }

            var bodyRead = await ReadRequestBodyAsync(httpContext.Request, registeredRoute.Config.MaxBodyBytes, ct);
            switch (bodyRead.Status)
            {
                case WebhookBodyReadStatus.TooLarge:
                    WebhookTelemetry.RecordBodyTooLarge(registeredRoute.Name);
                    logger.LogWarning(
                        "Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                        registeredRoute.Name, "body_too_large", remoteIp, (string?)null, (string?)null);
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                case WebhookBodyReadStatus.InvalidJson:
                    WebhookTelemetry.RecordInvalidJson(registeredRoute.Name);
                    logger.LogWarning(
                        "Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                        registeredRoute.Name, "invalid_json", remoteIp, (string?)null, (string?)null);
                    return Results.BadRequest(new { error = "Invalid JSON request body." });
            }

            var verification = verifier.Verify(registeredRoute, httpContext.Request.Headers, bodyRead.BodyBytes!);
            if (!verification.IsAccepted)
            {
                WebhookTelemetry.RecordVerificationFailed(registeredRoute.Name);
                logger.LogWarning(
                    "Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                    registeredRoute.Name, "verification_failed", remoteIp, verification.DeliveryId, verification.EventType);
                return Results.Unauthorized();
            }

            if (!registeredRoute.IsEventAllowed(verification.EventType))
            {
                WebhookTelemetry.RecordEventFiltered(registeredRoute.Name);
                logger.LogDebug(
                    "Webhook filtered route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                    registeredRoute.Name, "event_filtered", remoteIp, verification.DeliveryId, verification.EventType);
                return Results.Json(
                    new { status = "ignored", reason = "event_filtered" },
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
                return Results.Json(
                    new { status = "ignored", reason = "duplicate_delivery" },
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

                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var now = timeProvider.GetUtcNow();
            var deliveryKey = string.IsNullOrWhiteSpace(verification.DeliveryId)
                ? $"{now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"
                : verification.DeliveryId!;

            var sessionId = new SessionId($"webhook/{registeredRoute.Name}/{SanitizeWebhookId(deliveryKey)}");
            var invocation = new WebhookInvocation(
                registeredRoute,
                verification.EventType,
                verification.DeliveryId,
                bodyRead.BodyJson!,
                sessionId,
                now);

            WebhookTelemetry.RecordAccepted(registeredRoute.Name);
            logger.LogInformation(
                "Webhook accepted route={Route} reason={Reason} remote_ip={RemoteIp} delivery_id={DeliveryId} event_type={EventType}",
                registeredRoute.Name, "accepted", remoteIp, verification.DeliveryId, verification.EventType);

            notificationSink.Emit(new OperationalAlert
            {
                AlertId = Guid.NewGuid().ToString("N")[..12],
                Type = "webhook.received",
                Category = AlertType.WebhookReceived,
                Summary = $"Webhook '{registeredRoute.Name}' received event '{verification.EventType ?? "unknown"}'",
                Timestamp = now,
                Severity = "info",
                Source = registeredRoute.Name,
                Context = new Dictionary<string, string>
                {
                    ["route"] = registeredRoute.Name,
                    ["event"] = verification.EventType ?? "unknown",
                    ["deliveryId"] = verification.DeliveryId ?? "generated",
                    ["sessionId"] = sessionId.Value,
                }
            });

            executionService.StartInvocation(invocation);
            return Results.Json(
                new
                {
                    status = "accepted",
                    route = registeredRoute.Name,
                    eventType = verification.EventType,
                    deliveryId = verification.DeliveryId,
                    sessionId = sessionId.Value,
                },
                statusCode: StatusCodes.Status202Accepted);
        }).AllowAnonymous();

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
        var sanitized = new string(value.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray());

        return string.IsNullOrWhiteSpace(sanitized)
            ? Guid.NewGuid().ToString("N")
            : sanitized;
    }
}

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
