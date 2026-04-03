using System.IO;
using Microsoft.AspNetCore.Http;
using Netclaw.Configuration;

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
        WebhookVerifierKind.HeaderSecret => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(Config.Verification.Kind), Config.Verification.Kind, null)
    };

    public string SignaturePrefix => Config.Verification.Kind switch
    {
        WebhookVerifierKind.Hmac => string.IsNullOrWhiteSpace(Config.Verification.SignaturePrefix)
            ? string.Empty
            : Config.Verification.SignaturePrefix!,
        WebhookVerifierKind.HeaderSecret => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(Config.Verification.Kind), Config.Verification.Kind, null)
    };

    public string SecretHeaderName => Config.Verification.Kind switch
    {
        WebhookVerifierKind.Hmac => string.Empty,
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
        WebhookVerifierKind.HeaderSecret => string.IsNullOrWhiteSpace(Config.Verification.DeliveryIdHeaderName)
            ? "X-Webhook-Delivery"
            : Config.Verification.DeliveryIdHeaderName!,
        _ => throw new ArgumentOutOfRangeException(nameof(Config.Verification.Kind), Config.Verification.Kind, null)
    };

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
        if (Config.NotificationTarget is not { Kind: NotificationTargetKind.Slack, ChannelId: { Length: > 0 } channelId })
            return string.Empty;

        return $"If you need to notify a human, use send_slack_message to post to Slack channel {channelId}.";
    }

    public static string? GetHeaderValue(IHeaderDictionary headers, string name)
        => headers.TryGetValue(name, out var values)
            ? values.ToString()
            : null;
}
