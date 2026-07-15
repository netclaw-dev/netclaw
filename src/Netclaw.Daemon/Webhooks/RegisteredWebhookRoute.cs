// -----------------------------------------------------------------------
// <copyright file="RegisteredWebhookRoute.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO;
using Microsoft.AspNetCore.Http;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Webhooks;

public sealed record RegisteredWebhookRoute(string Name, string FilePath, DateTimeOffset LastModifiedUtc, WebhookRouteConfig Config)
{
    public string Path => $"/api/webhooks/{Name}";

    public string FileName => System.IO.Path.GetFileName(FilePath);

    public string SignatureHeaderName => Config.Verification.Kind switch
    {
        WebhookVerifierKind.Hmac => string.IsNullOrWhiteSpace(Config.Verification.SignatureHeaderName)
            ? "X-Webhook-Signature"
            : Config.Verification.SignatureHeaderName!,
        WebhookVerifierKind.HmacTimestamped => string.IsNullOrWhiteSpace(Config.Verification.SignatureHeaderName)
            ? "X-Webhook-Signature"
            : Config.Verification.SignatureHeaderName!,
        WebhookVerifierKind.HeaderSecret => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(Config.Verification.Kind), Config.Verification.Kind, null)
    };

    public string SignaturePrefix => Config.Verification.Kind switch
    {
        WebhookVerifierKind.Hmac => string.IsNullOrWhiteSpace(Config.Verification.SignaturePrefix)
            ? string.Empty
            : Config.Verification.SignaturePrefix!,
        WebhookVerifierKind.HmacTimestamped => string.Empty,
        WebhookVerifierKind.HeaderSecret => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(Config.Verification.Kind), Config.Verification.Kind, null)
    };

    public string SecretHeaderName => Config.Verification.Kind switch
    {
        WebhookVerifierKind.Hmac => string.Empty,
        WebhookVerifierKind.HmacTimestamped => string.Empty,
        WebhookVerifierKind.HeaderSecret => string.IsNullOrWhiteSpace(Config.Verification.SecretHeaderName)
            ? "X-Webhook-Secret"
            : Config.Verification.SecretHeaderName!,
        _ => throw new ArgumentOutOfRangeException(nameof(Config.Verification.Kind), Config.Verification.Kind, null)
    };

    public string EventHeaderName => Config.Verification.Kind switch
    {
        WebhookVerifierKind.Hmac => string.IsNullOrWhiteSpace(Config.Verification.EventHeaderName)
            ? "X-Webhook-Event"
            : Config.Verification.EventHeaderName!,
        WebhookVerifierKind.HmacTimestamped => string.IsNullOrWhiteSpace(Config.Verification.EventHeaderName)
            ? "X-Webhook-Event"
            : Config.Verification.EventHeaderName!,
        WebhookVerifierKind.HeaderSecret => string.IsNullOrWhiteSpace(Config.Verification.EventHeaderName)
            ? "X-Webhook-Event"
            : Config.Verification.EventHeaderName!,
        _ => throw new ArgumentOutOfRangeException(nameof(Config.Verification.Kind), Config.Verification.Kind, null)
    };

    public string DeliveryIdHeaderName => Config.Verification.Kind switch
    {
        WebhookVerifierKind.Hmac => string.IsNullOrWhiteSpace(Config.Verification.DeliveryIdHeaderName)
            ? "X-Webhook-Delivery"
            : Config.Verification.DeliveryIdHeaderName!,
        WebhookVerifierKind.HmacTimestamped => string.IsNullOrWhiteSpace(Config.Verification.DeliveryIdHeaderName)
            ? "X-Webhook-Delivery"
            : Config.Verification.DeliveryIdHeaderName!,
        WebhookVerifierKind.HeaderSecret => string.IsNullOrWhiteSpace(Config.Verification.DeliveryIdHeaderName)
            ? "X-Webhook-Delivery"
            : Config.Verification.DeliveryIdHeaderName!,
        _ => throw new ArgumentOutOfRangeException(nameof(Config.Verification.Kind), Config.Verification.Kind, null)
    };

    public int TimestampToleranceSeconds => Config.Verification.ToleranceSeconds ?? 300;

    public string TimestampField => Config.Verification.TimestampField ?? "t";

    public string TimestampSignatureField => Config.Verification.SignatureField ?? "v1";

    public string SignedPayloadSeparator => Config.Verification.SignedPayloadSeparator ?? ".";

    public bool IsEventAllowed(string? eventType)
    {
        if (Config.Events.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(eventType))
            return false;

        return Config.Events.Any(x => string.Equals(x, eventType, StringComparison.OrdinalIgnoreCase));
    }

    public string BuildPromptOverlay()
        => WebhookPromptBuilder.BuildOverlay(this);

    public string BuildDefaultNotifyInstructions()
    {
        var target = BuildNotificationDeliveryTarget();
        if (target is null)
            return string.Empty;

        return "If you need to notify a human, use send_channel_message with " +
               $"channel_key='{target.ChannelKey}', destination.channel_key='{target.ChannelKey}', " +
               $"destination.kind='{target.DestinationKind}', destination.id='{target.DestinationId}', and text set to your notification.";
    }

    public ChannelDeliveryTargetInfo? BuildNotificationDeliveryTarget()
    {
        if (Config.NotificationTarget is not { Kind: NotificationTargetKind.Slack, ChannelId: { Length: > 0 } channelId })
            return null;

        return new ChannelDeliveryTargetInfo(
            "slack",
            "destination",
            channelId,
            channelId);
    }

    public static string? GetHeaderValue(IHeaderDictionary headers, string name)
        => headers.TryGetValue(name, out var values)
            ? values.ToString()
            : null;
}
