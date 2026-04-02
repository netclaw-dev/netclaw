using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Webhooks;

public sealed record WebhookInvocation(
    RegisteredWebhookRoute Route,
    string? EventType,
    string? DeliveryId,
    string PayloadJson,
    SessionId SessionId,
    DateTimeOffset ReceivedAt);
