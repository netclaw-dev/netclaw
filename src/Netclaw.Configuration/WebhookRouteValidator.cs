// -----------------------------------------------------------------------
// <copyright file="WebhookRouteValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Shared validation logic for webhook route configurations.
/// Used by both CLI commands and doctor checks.
/// <para>
/// This is also the one place that enforces required-ness for a route. The
/// mutation message is a patch whose fields are nullable by design, so a route
/// gets its required fields checked here, on the merged definition, after the
/// patch is applied.
/// </para>
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

        if (!WebhookRouteName.TryCreate(routeName, out _, out var routeNameError))
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

        if (!Enum.IsDefined(route.Verification.Kind))
            errors.Add($"Verification.Kind value '{(int)route.Verification.Kind}' is not supported.");

        if (!Enum.IsDefined(route.Verification.HmacAlgorithm))
            errors.Add($"Verification.HmacAlgorithm value '{(int)route.Verification.HmacAlgorithm}' is not supported.");

        if (route.Verification.Kind == WebhookVerifierKind.HmacTimestamped)
        {
            if (route.Verification.ToleranceSeconds is < 1 or > 3600)
                errors.Add("Verification.ToleranceSeconds must be between 1 and 3600.");

            var timestampField = route.Verification.TimestampField ?? "t";
            var signatureField = route.Verification.SignatureField ?? "v1";
            ValidateStructuredHeaderField(errors, "TimestampField", timestampField);
            ValidateStructuredHeaderField(errors, "SignatureField", signatureField);

            if (string.Equals(timestampField, signatureField, StringComparison.Ordinal))
                errors.Add("Verification.TimestampField and Verification.SignatureField must be different.");
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
        => WebhookRouteName.TryCreate(routeName, out _, out var error)
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

    private static void ValidateStructuredHeaderField(
        List<string> errors,
        string propertyName,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Verification.{propertyName} cannot be blank.");
            return;
        }

        if (!value.All(IsHttpTokenCharacter))
            errors.Add($"Verification.{propertyName} must contain only HTTP token characters.");
    }

    private static bool IsHttpTokenCharacter(char value)
        => value is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-'
            or '.' or '^' or '_' or '`' or '|' or '~';
}
