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
        [property: Description("Verification kind: 'Hmac', 'HmacTimestamped', or 'HeaderSecret'.")]
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
        [property: Description("Audience: 'Public', 'Team', or 'Personal'. Omit to inherit the creating session/channel audience. A route may not exceed the creator's audience.")]
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
        bool? Enabled = null,
        [property: Description("Timestamp field name for HmacTimestamped routes. Defaults to 't'.")]
        string? TimestampField = null,
        [property: Description("Signature field name for HmacTimestamped routes. Defaults to 'v1'.")]
        string? SignatureField = null,
        [property: Description("Separator between timestamp and raw body for HmacTimestamped routes. Defaults to '.'.")]
        string? SignedPayloadSeparator = null,
        [property: Description("Accepted timestamp tolerance in seconds for HmacTimestamped routes, from 1 to 3600. Defaults to 300.")]
        int? ToleranceSeconds = null);

    public SetWebhookTool(WebhookRouteStore store)
    {
        _store = store;
    }

    protected override Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (!WebhookRouteStore.TryNormalizeRouteName(args.RouteName, out var routeName, out var routeError))
            return Task.FromResult($"Error: {routeError}");

        if (string.IsNullOrWhiteSpace(args.Prompt))
            return Task.FromResult("Error: 'prompt' is required.");
        if (string.IsNullOrWhiteSpace(args.Secret))
            return Task.FromResult("Error: 'secret' is required.");

        if (!WebhookRouteValidator.TryParseVerifierKind(args.VerificationKind, out var verificationKind))
            return Task.FromResult("Error: 'verificationKind' must be 'Hmac', 'HmacTimestamped', or 'HeaderSecret'.");

        if ((args.TimestampField is not null
             || args.SignatureField is not null
             || args.SignedPayloadSeparator is not null
             || args.ToleranceSeconds is not null)
            && verificationKind != WebhookVerifierKind.HmacTimestamped)
        {
            return Task.FromResult("Error: Timestamp signature settings require 'verificationKind' to be 'HmacTimestamped'.");
        }

        try
        {
            var result = _store.Update(
                routeName,
                ct,
                existing => BuildUpdate(routeName, args, context.Audience, verificationKind, existing));
            return Task.FromResult(result);
        }
        catch (InvalidDataException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
    }

    private static (WebhookRouteConfig? Definition, string Result) BuildUpdate(
        string routeName,
        Params args,
        TrustAudience creatorAudience,
        WebhookVerifierKind verificationKind,
        WebhookRouteConfig? existing)
    {
        if (existing is not null && existing.Audience > creatorAudience)
        {
            return (null,
                $"Error: Existing route audience '{existing.Audience.ToWireValue()}' exceeds creator authority ({creatorAudience.ToWireValue()}).");
        }

        TrustAudience audience;
        if (string.IsNullOrWhiteSpace(args.Audience) && existing is not null)
        {
            audience = existing.Audience;
        }
        else if (!TryResolveAudience(args.Audience, creatorAudience, out audience, out var audienceError))
        {
            return (null, audienceError!);
        }

        var existingVerification = existing?.Verification;

        var definition = new WebhookRouteConfig
        {
            Enabled = args.Enabled ?? existing?.Enabled ?? true,
            Prompt = args.Prompt.Trim(),
            Events = args.Events is null ? [.. existing?.Events ?? []] : ParseEvents(args.Events),
            Audience = audience,
            NotifyInstructions = args.NotifyInstructions?.Trim() ?? existing?.NotifyInstructions ?? string.Empty,
            DeliveryRequired = args.DeliveryRequired ?? existing?.DeliveryRequired ?? true,
            MaxBodyBytes = args.MaxBodyBytes ?? existing?.MaxBodyBytes ?? 1024 * 1024,
            RateLimitPerMinute = args.RateLimitPerMinute ?? existing?.RateLimitPerMinute ?? 30,
            Verification = new WebhookVerificationConfig
            {
                Kind = verificationKind,
                HmacAlgorithm = existingVerification?.HmacAlgorithm ?? WebhookHmacAlgorithm.Sha256,
                Secret = new SensitiveString(args.Secret),
                SignatureHeaderName = args.SignatureHeaderName is null
                    ? existingVerification?.SignatureHeaderName
                    : NormalizeOptional(args.SignatureHeaderName),
                SignaturePrefix = args.SignaturePrefix is null
                    ? existingVerification?.SignaturePrefix
                    : NormalizeOptional(args.SignaturePrefix, trim: false),
                SecretHeaderName = args.SecretHeaderName is null
                    ? existingVerification?.SecretHeaderName
                    : NormalizeOptional(args.SecretHeaderName),
                EventHeaderName = args.EventHeaderName is null
                    ? existingVerification?.EventHeaderName
                    : NormalizeOptional(args.EventHeaderName),
                DeliveryIdHeaderName = args.DeliveryIdHeaderName is null
                    ? existingVerification?.DeliveryIdHeaderName
                    : NormalizeOptional(args.DeliveryIdHeaderName),
                TimestampField = args.TimestampField is null
                    ? existingVerification?.TimestampField
                    : args.TimestampField,
                SignatureField = args.SignatureField is null
                    ? existingVerification?.SignatureField
                    : args.SignatureField,
                SignedPayloadSeparator = args.SignedPayloadSeparator ?? existingVerification?.SignedPayloadSeparator,
                ToleranceSeconds = args.ToleranceSeconds ?? existingVerification?.ToleranceSeconds
            }
        };

        if (args.NotificationChannelId is null && existing?.NotificationTarget is { } existingTarget)
        {
            definition.NotificationTarget = new NotificationTargetConfig
            {
                Kind = existingTarget.Kind,
                ChannelId = existingTarget.ChannelId
            };
        }
        else if (!string.IsNullOrWhiteSpace(args.NotificationChannelId))
        {
            definition.NotificationTarget = new NotificationTargetConfig
            {
                Kind = NotificationTargetKind.Slack,
                ChannelId = args.NotificationChannelId.Trim()
            };
        }

        var validationErrors = WebhookRouteValidator.Validate(routeName, definition);
        if (validationErrors.Count > 0)
            return (null, $"Error: {validationErrors[0]}");

        return (definition,
            $"Webhook route '{routeName}' saved at /api/webhooks/{routeName}. Secret stored in the route file; keep it aligned with the sender configuration.");
    }

    /// <summary>
    /// Resolves the route's audience from the optional explicit argument, falling
    /// back to the creating context's audience (transitive provenance, mirroring
    /// <c>set_reminder</c>). A route may not be minted above the creator's
    /// authority — downgrade-only, mirroring
    /// <c>ReminderManagerActor.ValidateRequestedAudience</c>. A context-less
    /// invocation carries the unbound tool scope's
    /// <see cref="TrustAudience.Public"/>, so it cannot escalate. (Routes defined
    /// directly in config never reach this tool; they keep
    /// <c>WebhooksConfig.Audience</c>'s <see cref="TrustAudience.Public"/> default.)
    /// </summary>
    private static bool TryResolveAudience(string? requested, TrustAudience creatorAudience, out TrustAudience audience, out string? error)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            audience = creatorAudience;
            error = null;
            return true;
        }

        if (!SecurityPolicyDefaults.TryParseAudience(requested, out var parsed))
        {
            audience = creatorAudience;
            error = "Error: 'audience' must be Public, Team, or Personal.";
            return false;
        }

        if (parsed > creatorAudience)
        {
            audience = creatorAudience;
            error = $"Error: Requested audience '{parsed.ToWireValue()}' exceeds creator authority ({creatorAudience.ToWireValue()}).";
            return false;
        }

        audience = parsed;
        error = null;
        return true;
    }

    private static List<string> ParseEvents(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !string.IsNullOrWhiteSpace(x))];
    }

    private static string? NormalizeOptional(string value, bool trim = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return trim ? value.Trim() : value;
    }
}
