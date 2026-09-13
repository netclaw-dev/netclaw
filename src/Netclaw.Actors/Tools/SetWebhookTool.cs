// -----------------------------------------------------------------------
// <copyright file="SetWebhookTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Akka.Actor;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Webhooks.WebhookRouteProtocol;

namespace Netclaw.Actors.Tools;

[NetclawTool("set_webhook",
    "Create or update an inbound webhook route stored in Netclaw's webhook config directory. Use this instead of raw file access for webhook definitions.",
    Grant = "webhook_admin")]
public sealed partial class SetWebhookTool : NetclawTool<SetWebhookTool.Params>
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(10);

    private readonly IActorRef _routeActor;

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

    public SetWebhookTool(IActorRef routeActor)
    {
        _routeActor = routeActor;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        // The tool front parses the wire string once. Past this line the route
        // name is a WebhookRouteName, so no later step can reach a file with an
        // unvalidated name.
        if (!WebhookRouteName.TryCreate(args.RouteName, out var routeName, out var routeError))
            return $"Error: {routeError}";

        if (string.IsNullOrWhiteSpace(args.Prompt))
            return "Error: 'prompt' is required.";
        if (string.IsNullOrWhiteSpace(args.Secret))
            return "Error: 'secret' is required.";

        if (!WebhookRouteValidator.TryParseVerifierKind(args.VerificationKind, out var verificationKind))
            return "Error: 'verificationKind' must be 'Hmac', 'HmacTimestamped', or 'HeaderSecret'.";

        if ((args.TimestampField is not null
             || args.SignatureField is not null
             || args.SignedPayloadSeparator is not null
             || args.ToleranceSeconds is not null)
            && verificationKind != WebhookVerifierKind.HmacTimestamped)
        {
            return "Error: Timestamp signature settings require 'verificationKind' to be 'HmacTimestamped'.";
        }

        if (!TryResolveRequestedAudience(args.Audience, out var requestedAudience, out var audienceError))
            return audienceError!;

        try
        {
            var response = await _routeActor.Ask<RouteSaved>(
                BuildCommand(routeName, args, context.Audience, verificationKind, requestedAudience),
                AskTimeout,
                ct);

            return response.Success
                ? $"Webhook route '{routeName.Value}' saved at /api/webhooks/{routeName.Value}. Secret stored in the route file; keep it aligned with the sender configuration."
                : $"Error: {response.ErrorMessage}";
        }
        catch (InvalidDataException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (TimeoutException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Projects the tool arguments into the actor's field-level patch. The tool
    /// owns the wire grammar — comma-separated events, the audience and
    /// verification-kind spellings — and the actor owns the merge, the audience
    /// authority check, and validation.
    /// </summary>
    private static UpsertRoute BuildCommand(
        WebhookRouteName routeName,
        Params args,
        TrustAudience creatorAudience,
        WebhookVerifierKind verificationKind,
        TrustAudience? requestedAudience) => new()
        {
            RouteName = routeName,
            CreatorAudience = creatorAudience,
            RequestedAudience = requestedAudience,
            Prompt = args.Prompt,
            Secret = args.Secret,
            VerificationKind = verificationKind,
            Events = args.Events is null ? null : ParseEvents(args.Events),
            NotifyInstructions = args.NotifyInstructions,
            DeliveryRequired = args.DeliveryRequired,
            NotificationChannelId = args.NotificationChannelId,
            MaxBodyBytes = args.MaxBodyBytes,
            RateLimitPerMinute = args.RateLimitPerMinute,
            Enabled = args.Enabled,
            SignatureHeaderName = args.SignatureHeaderName,
            SignaturePrefix = args.SignaturePrefix,
            SecretHeaderName = args.SecretHeaderName,
            EventHeaderName = args.EventHeaderName,
            DeliveryIdHeaderName = args.DeliveryIdHeaderName,
            TimestampField = args.TimestampField,
            SignatureField = args.SignatureField,
            SignedPayloadSeparator = args.SignedPayloadSeparator,
            ToleranceSeconds = args.ToleranceSeconds
        };

    /// <summary>
    /// Parses the optional explicit audience argument. A blank argument leaves
    /// the audience unrequested, so the actor inherits the stored route's
    /// audience or the creating context's audience (transitive provenance,
    /// mirroring <c>set_reminder</c>). The actor enforces the downgrade-only
    /// rule: a route may not be minted above the creator's authority. A
    /// context-less invocation carries the unbound tool scope's
    /// <see cref="TrustAudience.Public"/>, so it cannot escalate. (Routes defined
    /// directly in config never reach this tool; they keep
    /// <c>WebhooksConfig.Audience</c>'s <see cref="TrustAudience.Public"/> default.)
    /// </summary>
    private static bool TryResolveRequestedAudience(string? requested, out TrustAudience? audience, out string? error)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            audience = null;
            error = null;
            return true;
        }

        if (!SecurityPolicyDefaults.TryParseAudience(requested, out var parsed))
        {
            audience = null;
            error = "Error: 'audience' must be Public, Team, or Personal.";
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
}
