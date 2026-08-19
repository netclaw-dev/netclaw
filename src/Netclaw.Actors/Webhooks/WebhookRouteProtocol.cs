// -----------------------------------------------------------------------
// <copyright file="WebhookRouteProtocol.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Configuration;

namespace Netclaw.Actors.Webhooks;

/// <summary>
/// Message contract for <see cref="WebhookRouteActor"/>, the single webhook
/// route mutation authority inside the daemon.
/// <para>
/// Every message is local-only. The actor holds no cluster identity and the
/// payloads carry mutable <see cref="WebhookRouteConfig"/> instances, so each
/// message opts out of serialization verification.
/// </para>
/// </summary>
public static class WebhookRouteProtocol
{
    /// <summary>Marker for webhook route mutations.</summary>
    public interface IWebhookRouteCommand;

    /// <summary>Marker for webhook route reads.</summary>
    public interface IWebhookRouteQuery;

    /// <summary>Marker for webhook route replies.</summary>
    public interface IWebhookRouteResponse;

    // ===== Commands =====

    /// <summary>
    /// Field-level route patch. The actor reads the stored route, applies the
    /// fields this message carries, validates the merged definition, and writes
    /// it back — all inside one message turn.
    /// <para>
    /// The mutation travels as DATA, never as a delegate. A null property means
    /// "leave the stored value unchanged", which is what both <c>set_webhook</c>
    /// and <c>netclaw webhooks set</c> already mean by an omitted argument. Two
    /// concurrent patches of different fields therefore compose instead of
    /// overwriting each other.
    /// </para>
    /// </summary>
    public sealed record UpsertRoute : IWebhookRouteCommand, INoSerializationVerificationNeeded
    {
        /// <summary>Route name. The actor normalizes it before any file access.</summary>
        public required string RouteName { get; init; }

        /// <summary>
        /// Authority of the caller that requested the mutation. A route may not
        /// be minted or updated above this audience — the downgrade-only guard
        /// that keeps a low-authority session from taking over a high-authority
        /// route.
        /// </summary>
        public required TrustAudience CreatorAudience { get; init; }

        /// <summary>
        /// Audience explicitly requested for the route. Null inherits the stored
        /// audience, or <see cref="CreatorAudience"/> for a new route.
        /// </summary>
        public TrustAudience? RequestedAudience { get; init; }

        public string? Prompt { get; init; }

        public string? Secret { get; init; }

        public WebhookVerifierKind? VerificationKind { get; init; }

        public IReadOnlyList<string>? Events { get; init; }

        public string? NotifyInstructions { get; init; }

        public bool? DeliveryRequired { get; init; }

        /// <summary>
        /// Slack channel for human-facing notifications. A blank (but non-null)
        /// value clears the stored notification target.
        /// </summary>
        public string? NotificationChannelId { get; init; }

        public int? MaxBodyBytes { get; init; }

        public int? RateLimitPerMinute { get; init; }

        public bool? Enabled { get; init; }

        public string? SignatureHeaderName { get; init; }

        public string? SignaturePrefix { get; init; }

        public string? SecretHeaderName { get; init; }

        public string? EventHeaderName { get; init; }

        public string? DeliveryIdHeaderName { get; init; }

        public string? TimestampField { get; init; }

        public string? SignatureField { get; init; }

        public string? SignedPayloadSeparator { get; init; }

        public int? ToleranceSeconds { get; init; }
    }

    /// <summary>Removes one route file.</summary>
    public sealed record DeleteRoute(string RouteName)
        : IWebhookRouteCommand, INoSerializationVerificationNeeded;

    // ===== Queries =====

    /// <summary>Reads one route from disk.</summary>
    public sealed record GetRoute(string RouteName)
        : IWebhookRouteQuery, INoSerializationVerificationNeeded;

    /// <summary>Reads every route file from disk.</summary>
    public sealed record ListRoutes : IWebhookRouteQuery, INoSerializationVerificationNeeded
    {
        public static readonly ListRoutes Instance = new();
    }

    // ===== Responses =====

    /// <summary>Why a route mutation failed.</summary>
    public enum WebhookRouteError
    {
        None = 0,

        /// <summary>The route name or the merged definition failed validation.</summary>
        Validation = 1,

        /// <summary>The caller lacks the authority for the requested audience.</summary>
        Authority = 2
    }

    /// <summary>
    /// Outcome of an <see cref="UpsertRoute"/>. <paramref name="Route"/> carries
    /// the stored definition on success, including the secret, so callers that
    /// project it to an external surface must strip the secret first.
    /// </summary>
    public sealed record RouteSaved(
        string RouteName,
        bool Success,
        bool Created,
        WebhookRouteConfig? Route,
        WebhookRouteError Error = WebhookRouteError.None,
        string? ErrorMessage = null) : IWebhookRouteResponse, INoSerializationVerificationNeeded;

    /// <summary>Outcome of a <see cref="DeleteRoute"/>.</summary>
    public sealed record RouteDeleted(string RouteName, bool Found)
        : IWebhookRouteResponse, INoSerializationVerificationNeeded;

    /// <summary>
    /// Outcome of a <see cref="GetRoute"/>. <paramref name="Found"/> reports
    /// whether the file exists; a found route with a null
    /// <paramref name="Route"/> is a file that exists but does not parse.
    /// </summary>
    public sealed record RouteResponse(string RouteName, bool Found, WebhookRouteConfig? Route)
        : IWebhookRouteResponse, INoSerializationVerificationNeeded;

    /// <summary>
    /// One entry of a <see cref="RouteListResponse"/>. A null
    /// <paramref name="Definition"/> is a route file that does not parse.
    /// </summary>
    public sealed record RouteEntry(string RouteName, WebhookRouteConfig? Definition);

    /// <summary>Outcome of a <see cref="ListRoutes"/>.</summary>
    public sealed record RouteListResponse(IReadOnlyList<RouteEntry> Routes)
        : IWebhookRouteResponse, INoSerializationVerificationNeeded;
}
