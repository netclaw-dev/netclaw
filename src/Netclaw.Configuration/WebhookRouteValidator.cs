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

        if (string.IsNullOrWhiteSpace(routeName))
            errors.Add("Route name must not be empty.");

        if (string.IsNullOrWhiteSpace(route.Prompt))
            errors.Add("Prompt is required.");

        if (route.Verification.Secret is null || string.IsNullOrWhiteSpace(route.Verification.Secret.Value))
            errors.Add("Verification secret is required.");

        if (route.MaxBodyBytes < 1)
            errors.Add("MaxBodyBytes must be >= 1.");

        if (route.RateLimitPerMinute < 1)
            errors.Add("RateLimitPerMinute must be >= 1.");

        if (route.Events.Any(string.IsNullOrWhiteSpace))
            errors.Add("Events list contains a blank entry.");

        if (route.DeliveryRequired
            && route.NotificationTarget is null
            && !string.IsNullOrWhiteSpace(route.NotifyInstructions))
        {
            errors.Add("NotificationTarget is required when DeliveryRequired is true and NotifyInstructions are provided.");
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
}
