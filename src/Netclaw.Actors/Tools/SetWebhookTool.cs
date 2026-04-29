// -----------------------------------------------------------------------
// <copyright file="SetWebhookTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

[NetclawTool("set_webhook",
    "Create or update an inbound webhook route stored in Netclaw's webhook config directory. Use this instead of raw file access for webhook definitions.",
    Grant = "webhook_admin")]
public sealed partial class SetWebhookTool : NetclawTool<SetWebhookTool.Params>
{
    private readonly WebhookRouteStore _store;

    public record Params(
        [property: Description("Stable route name used in the webhook URL path (kebab-case, for example 'github-issues').")]
        string RouteName,
        [property: Description("Prompt overlay instructions for this route.")]
        string Prompt,
        [property: Description("Verification kind: 'Hmac' or 'HeaderSecret'.")]
        string VerificationKind,
        [property: Description("Shared secret used to verify incoming requests.")]
        string Secret,
        [property: Description("Optional HMAC signature header name. Defaults to X-Webhook-Signature for Hmac routes.")]
        string? SignatureHeaderName = null,
        [property: Description("Optional HMAC signature prefix such as 'sha256='. Leave empty for raw hex signatures.")]
        string? SignaturePrefix = null,
        [property: Description("Optional secret header name for HeaderSecret routes. Defaults to X-Webhook-Secret.")]
        string? SecretHeaderName = null,
        [property: Description("Optional event header name. Defaults depend on verification kind.")]
        string? EventHeaderName = null,
        [property: Description("Optional delivery ID header name. Defaults depend on verification kind.")]
        string? DeliveryIdHeaderName = null,
        [property: Description("Optional comma-separated event allowlist.")]
        string? Events = null,
        [property: Description("Audience: 'Public', 'Team', or 'Personal'.")]
        string? Audience = null,
        [property: Description("Optional notification instructions appended to the route overlay.")]
        string? NotifyInstructions = null,
        [property: Description("Whether notification delivery is required when notification instructions are present. Defaults to true.")]
        bool? DeliveryRequired = null,
        [property: Description("Optional Slack channel ID for human-facing notifications.")]
        string? NotificationChannelId = null,
        [property: Description("Maximum accepted request body size in bytes.")]
        int? MaxBodyBytes = null,
        [property: Description("Per-route accepted requests per minute.")]
        int? RateLimitPerMinute = null,
        [property: Description("Whether this route is enabled. Defaults to true.")]
        bool? Enabled = null);

    public SetWebhookTool(WebhookRouteStore store)
    {
        _store = store;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (!WebhookRouteStore.TryNormalizeRouteName(args.RouteName, out var routeName, out var routeError))
            return Task.FromResult($"Error: {routeError}");

        if (string.IsNullOrWhiteSpace(args.Prompt))
            return Task.FromResult("Error: 'prompt' is required.");
        if (string.IsNullOrWhiteSpace(args.Secret))
            return Task.FromResult("Error: 'secret' is required.");

        if (!Enum.TryParse<WebhookVerifierKind>(args.VerificationKind, ignoreCase: true, out var verificationKind))
            return Task.FromResult("Error: 'verificationKind' must be 'Hmac' or 'HeaderSecret'.");

        if (!TryParseAudience(args.Audience, out var audience, out var audienceError))
            return Task.FromResult(audienceError!);

        var definition = new WebhookRouteConfig
        {
            Enabled = args.Enabled ?? true,
            Prompt = args.Prompt.Trim(),
            Events = ParseEvents(args.Events),
            Audience = audience,
            NotifyInstructions = args.NotifyInstructions?.Trim() ?? string.Empty,
            DeliveryRequired = args.DeliveryRequired ?? true,
            MaxBodyBytes = args.MaxBodyBytes ?? 1024 * 1024,
            RateLimitPerMinute = args.RateLimitPerMinute ?? 30,
            Verification = new WebhookVerificationConfig
            {
                Kind = verificationKind,
                Secret = new SensitiveString(args.Secret),
                SignatureHeaderName = string.IsNullOrWhiteSpace(args.SignatureHeaderName) ? null : args.SignatureHeaderName.Trim(),
                SignaturePrefix = string.IsNullOrWhiteSpace(args.SignaturePrefix) ? null : args.SignaturePrefix,
                SecretHeaderName = string.IsNullOrWhiteSpace(args.SecretHeaderName) ? null : args.SecretHeaderName.Trim(),
                EventHeaderName = string.IsNullOrWhiteSpace(args.EventHeaderName) ? null : args.EventHeaderName.Trim(),
                DeliveryIdHeaderName = string.IsNullOrWhiteSpace(args.DeliveryIdHeaderName) ? null : args.DeliveryIdHeaderName.Trim(),
            }
        };

        if (!string.IsNullOrWhiteSpace(args.NotificationChannelId))
        {
            definition.NotificationTarget = new NotificationTargetConfig
            {
                Kind = NotificationTargetKind.Slack,
                ChannelId = args.NotificationChannelId.Trim()
            };
        }

        _store.Save(routeName, definition);
        return Task.FromResult($"Webhook route '{routeName}' saved at /api/webhooks/{routeName}. Secret stored in the route file; keep it aligned with the sender configuration.");
    }

    private static bool TryParseAudience(string? value, out TrustAudience audience, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            audience = TrustAudience.Public;
            error = null;
            return true;
        }

        if (Enum.TryParse<TrustAudience>(value, ignoreCase: true, out audience))
        {
            error = null;
            return true;
        }

        audience = TrustAudience.Public;
        error = "Error: 'audience' must be Public, Team, or Personal.";
        return false;
    }

    private static List<string> ParseEvents(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }
}
