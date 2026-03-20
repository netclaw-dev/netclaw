using System.Net;

namespace Netclaw.Configuration;

public static class NotificationConfigValidator
{
    private const int MaxDeduplicationWindowSeconds = 86_400;
    private const int MaxRetries = 5;
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 60;

    private static readonly HashSet<string> AuthLikeHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Api-Key",
        "ApiKey",
        "X-Api-Key",
        "X-Auth-Token",
        "X-Auth-Key"
    };

    public static NotificationConfigValidationResult Validate(NotificationsConfig? config)
    {
        if (config is null)
            return NotificationConfigValidationResult.Valid;

        var issues = new List<NotificationConfigValidationIssue>();

        ValidateRange(
            issues,
            fieldPath: "Notifications.DeduplicationWindowSeconds",
            value: config.DeduplicationWindowSeconds,
            min: 0,
            max: MaxDeduplicationWindowSeconds,
            remediation: $"Use a value between 0 and {MaxDeduplicationWindowSeconds} seconds.");

        ValidateRange(
            issues,
            fieldPath: "Notifications.MaxRetries",
            value: config.MaxRetries,
            min: 0,
            max: MaxRetries,
            remediation: $"Use a value between 0 and {MaxRetries} retries.");

        ValidateRange(
            issues,
            fieldPath: "Notifications.TimeoutSeconds",
            value: config.TimeoutSeconds,
            min: MinTimeoutSeconds,
            max: MaxTimeoutSeconds,
            remediation: $"Use a value between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.");

        for (var i = 0; i < config.Webhooks.Count; i++)
            ValidateTarget(config.Webhooks[i], i, issues);

        return issues.Count == 0 ? NotificationConfigValidationResult.Valid : new NotificationConfigValidationResult(issues);
    }

    public static bool IsAuthLikeHeaderName(string? headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
            return false;

        var normalized = NormalizeHeaderName(headerName);
        return AuthLikeHeaderNames.Contains(headerName) || normalized is "authorization" or "proxyauthorization" or "apikey" or "authtoken" or "authkey";
    }

    public static string FormatTargetIdentity(WebhookTarget target, int index)
    {
        var sanitizedUrl = SanitizeUrlForDisplay(target.Url?.Value);

        if (!string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(sanitizedUrl))
            return $"{target.Name} ({sanitizedUrl})";

        if (!string.IsNullOrWhiteSpace(target.Name))
            return target.Name!;

        if (!string.IsNullOrWhiteSpace(sanitizedUrl))
            return sanitizedUrl;

        return $"webhook[{index}]";
    }

    public static string FormatRedactedHeaders(IReadOnlyDictionary<string, SensitiveString>? headers)
    {
        if (headers is null || headers.Count == 0)
            return "none";

        return string.Join(", ", headers.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).Select(static key => $"{key}=<redacted>"));
    }

    public static string SanitizeUrlForDisplay(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "<invalid-url>";

        var authority = uri.GetLeftPart(UriPartial.Authority);
        return string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
            ? authority + "/"
            : authority + "/<redacted>";
    }

    private static void ValidateTarget(WebhookTarget target, int index, List<NotificationConfigValidationIssue> issues)
    {
        var fieldPath = $"Notifications.Webhooks[{index}].Url";
        var url = target.Url?.Value;

        if (string.IsNullOrWhiteSpace(url))
        {
            issues.Add(new NotificationConfigValidationIssue(
                fieldPath,
                "Webhook URL is required.",
                "Set an absolute https:// URL or use http:// only for localhost, 127.0.0.1, or ::1 during local development."));
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            issues.Add(new NotificationConfigValidationIssue(
                fieldPath,
                "Webhook URL must be a valid absolute URI.",
                "Fix the URL so it includes a scheme and host, for example https://alerts.example/hooks/netclaw."));
            return;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            issues.Add(new NotificationConfigValidationIssue(
                fieldPath,
                "Webhook URL must not include a fragment.",
                "Remove the #fragment portion from the webhook URL."));
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return;

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            if (IsLoopbackHost(uri.Host))
                return;

            issues.Add(new NotificationConfigValidationIssue(
                fieldPath,
                "Non-loopback plaintext HTTP webhook targets are not allowed.",
                "Switch to https:// or limit http:// targets to localhost, 127.0.0.1, or ::1 for local development."));
            return;
        }

        issues.Add(new NotificationConfigValidationIssue(
            fieldPath,
            $"Unsupported webhook URL scheme '{uri.Scheme}'.",
            "Use https:// for normal targets or http:// only for localhost, 127.0.0.1, or ::1."));
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static string NormalizeHeaderName(string headerName)
    {
        return new string(headerName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static void ValidateRange(
        List<NotificationConfigValidationIssue> issues,
        string fieldPath,
        int value,
        int min,
        int max,
        string remediation)
    {
        if (value >= min && value <= max)
            return;

        issues.Add(new NotificationConfigValidationIssue(
            fieldPath,
            $"Value {value} is outside the supported range {min} to {max}.",
            remediation));
    }
}
