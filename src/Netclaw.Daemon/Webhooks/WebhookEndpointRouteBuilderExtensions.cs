using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Webhooks;

public static class WebhookEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
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
            if (!routeCatalog.TryGetRoute(route, out var registeredRoute))
                return Results.NotFound();

            var bodyRead = await ReadRequestBodyAsync(httpContext.Request, registeredRoute.Config.MaxBodyBytes, ct);
            if (!bodyRead.Success)
            {
                return bodyRead.Reason switch
                {
                    "body_too_large" => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
                    _ => Results.BadRequest(new { error = "Invalid JSON request body." })
                };
            }

            var verification = verifier.Verify(registeredRoute, httpContext.Request.Headers, bodyRead.BodyBytes!);
            if (!verification.IsAccepted)
                return Results.Unauthorized();

            if (!registeredRoute.IsEventAllowed(verification.EventType))
            {
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
                return Results.Json(
                    new { status = "ignored", reason = "duplicate_delivery" },
                    statusCode: StatusCodes.Status202Accepted);
            }

            if (guardDecision.Kind == WebhookIngressDecisionKind.RateLimited)
            {
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

internal sealed record WebhookBodyReadResult(bool Success, string? Reason, byte[]? BodyBytes, string? BodyJson)
{
    public static WebhookBodyReadResult Ok(byte[] bodyBytes, string bodyJson) => new(true, null, bodyBytes, bodyJson);
    public static WebhookBodyReadResult TooLarge() => new(false, "body_too_large", null, null);
    public static WebhookBodyReadResult InvalidJson() => new(false, "invalid_json", null, null);
}
