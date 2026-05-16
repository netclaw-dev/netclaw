// -----------------------------------------------------------------------
// <copyright file="WebhookEventType.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Daemon.Webhooks;

/// <summary>
/// Strongly-typed webhook event-type discriminator — the provider-supplied
/// event name (e.g. <c>push</c>, <c>issues.opened</c>) extracted from an
/// inbound webhook header and used for event-filter matching. Wraps the raw
/// string so an event type cannot be confused with a <see cref="WebhookDeliveryId"/>
/// or any other string at a call boundary.
/// </summary>
public readonly record struct WebhookEventType(string Value)
{
    public static explicit operator WebhookEventType(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>
/// Strongly-typed webhook delivery correlation id — the provider-supplied
/// unique delivery identifier extracted from an inbound webhook header and used
/// for duplicate-delivery suppression. Wraps the raw string so a delivery id
/// cannot be confused with a <see cref="WebhookEventType"/> or any other string
/// at a call boundary.
/// </summary>
public readonly record struct WebhookDeliveryId(string Value)
{
    public static explicit operator WebhookDeliveryId(string value) => new(value);

    public override string ToString() => Value;
}
