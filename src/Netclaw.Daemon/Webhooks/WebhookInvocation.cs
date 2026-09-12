// -----------------------------------------------------------------------
// <copyright file="WebhookInvocation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Webhooks;

public sealed record WebhookInvocation(
    RegisteredWebhookRoute Route,
    WebhookEventType? EventType,
    WebhookDeliveryId? DeliveryId,
    string PayloadJson,
    SessionId SessionId,
    DateTimeOffset ReceivedAt);
