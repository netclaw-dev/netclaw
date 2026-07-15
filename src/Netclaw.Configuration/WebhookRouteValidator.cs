// -----------------------------------------------------------------------
// <copyright file="WebhookRouteValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Shared validation logic for webhook route configurations.
/// Used by both CLI commands and doctor checks.
/// </summary>
public static class WebhookRouteValidator
{
    /// <summary>
    /// Validates a webhook route configuration and returns a list of validation errors.
    /// Returns an empty list if the route is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(string routeName, WebhookRouteConfig route)
    {
        var errors = new List<string>();

        if (route is null)
        {
            errors.Add("Webhook route definition is missing.");
            return errors;
        }

        if (!WebhookRouteStore.TryNormalizeRouteName(routeName, out _, out var routeNameError))
            errors.Add(routeNameError!);

        if (route.Verification is null)
        {
            errors.Add("Verification settings are required.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(route.Prompt))
            errors.Add("Prompt is required.");

        if (route.Verification.Secret.IsNullOrEmpty())
            errors.Add("Verification secret is required.");

        if (route.Verification.Kind == WebhookVerifierKind.HmacTimestamped)
        {
            if (route.Verification.ToleranceSeconds is < 1 or > 3600)
                errors.Add("Verification.ToleranceSeconds must be between 1 and 3600.");

            if (route.Verification.TimestampField is { } timestampField
                && string.IsNullOrWhiteSpace(timestampField))
            {
                errors.Add("Verification.TimestampField cannot be blank.");
            }

            if (route.Verification.SignatureField is { } signatureField
                && string.IsNullOrWhiteSpace(signatureField))
            {
                errors.Add("Verification.SignatureField cannot be blank.");
            }
        }

        if (route.MaxBodyBytes < 1)
            errors.Add("MaxBodyBytes must be >= 1.");

        if (route.RateLimitPerMinute < 1)
            errors.Add("RateLimitPerMinute must be >= 1.");

        if (route.Events.Any(string.IsNullOrWhiteSpace))
            errors.Add("Events list contains a blank entry.");

        if (route.NotificationTarget is null
            && !string.IsNullOrWhiteSpace(route.NotifyInstructions))
        {
            errors.Add("NotificationTarget is required when NotifyInstructions are provided.");
        }

        if (route.NotificationTarget is { Kind: NotificationTargetKind.Slack } target
            && string.IsNullOrWhiteSpace(target.ChannelId))
        {
            errors.Add("NotificationTarget.ChannelId is required for Slack targets.");
        }

        return errors;
    }

    /// <summary>
    /// Validates a webhook route and throws an <see cref="InvalidOperationException"/>
    /// with the first validation error if invalid.
    /// </summary>
    public static void ValidateOrThrow(string routeName, WebhookRouteConfig route)
    {
        var errors = Validate(routeName, route);
        if (errors.Count > 0)
            throw new InvalidOperationException(errors[0]);
    }

    /// <summary>
    /// Validates a route name and returns a user-facing error if invalid.
    /// </summary>
    public static string? ValidateRouteName(string routeName)
        => WebhookRouteStore.TryNormalizeRouteName(routeName, out _, out var error)
            ? null
            : error;

    public static bool TryParseVerifierKind(string value, out WebhookVerifierKind kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "hmac":
                kind = WebhookVerifierKind.Hmac;
                return true;
            case "header-secret":
            case "headersecret":
                kind = WebhookVerifierKind.HeaderSecret;
                return true;
            case "hmac-timestamped":
            case "hmactimestamped":
                kind = WebhookVerifierKind.HmacTimestamped;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
