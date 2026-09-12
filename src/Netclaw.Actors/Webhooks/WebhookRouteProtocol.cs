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
    /// <remarks>
    /// Almost every field is nullable because this is a patch, not a full
    /// definition. Null does not mean "absent"; it means "keep what the file
    /// holds". A required field here would force every caller to resend values
    /// it does not want to change, and the resend would overwrite a concurrent
    /// patch of that same field.
    /// <para>
    /// Required-ness belongs to the merged definition instead, and
    /// <see cref="WebhookRouteValidator"/> is the one place that enforces it.
    /// A route needs a prompt and a verification secret, so the validator
    /// rejects a merged definition without them, whatever the patch omitted.
    /// The two fields that this message does require are the ones a patch can
    /// never inherit from a file: the route to patch, and the authority of the
    /// caller.
    /// </para>
    /// </remarks>
    /// </summary>
    public sealed record UpsertRoute : IWebhookRouteCommand, INoSerializationVerificationNeeded
    {
        /// <summary>The route to create or to patch.</summary>
        public required WebhookRouteName RouteName { get; init; }

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
    public sealed record DeleteRoute(WebhookRouteName RouteName)
        : IWebhookRouteCommand, INoSerializationVerificationNeeded;

    // ===== Queries =====

    /// <summary>Reads one route from disk.</summary>
    public sealed record GetRoute(WebhookRouteName RouteName)
        : IWebhookRouteQuery, INoSerializationVerificationNeeded;

    /// <summary>Reads every route file from disk.</summary>
    public sealed record ListRoutes : IWebhookRouteQuery, INoSerializationVerificationNeeded
    {
        public static readonly ListRoutes Instance = new();
    }

    // ===== Responses =====

    /// <summary>
    /// What the actor did with an <see cref="UpsertRoute"/>. The four states are
    /// exclusive, so one enum replaces the success flag, the created flag, and
    /// the separate error code that could disagree with each other.
    /// </summary>
    public enum RouteSaveOutcome
    {
        /// <summary>The actor wrote a route file that did not exist before.</summary>
        Created = 0,

        /// <summary>The actor merged the patch into an existing route file.</summary>
        Updated = 1,

        /// <summary>The merged definition failed validation. No file changed.</summary>
        ValidationRejected = 2,

        /// <summary>The caller lacks the authority for the route. No file changed.</summary>
        AuthorityRejected = 3
    }

    /// <summary>
    /// Outcome of an <see cref="UpsertRoute"/>. <paramref name="Route"/> carries
    /// the stored definition on success, including the secret, so callers that
    /// project it to an external surface must strip the secret first. A
    /// rejection carries a null route and the operator-facing reason.
    /// </summary>
    public sealed record RouteSaved(
        WebhookRouteName RouteName,
        RouteSaveOutcome Outcome,
        WebhookRouteConfig? Route,
        string? ErrorMessage = null) : IWebhookRouteResponse, INoSerializationVerificationNeeded
    {
        /// <summary>True when the actor wrote the route file.</summary>
        public bool Success => Outcome is RouteSaveOutcome.Created or RouteSaveOutcome.Updated;
    }

    /// <summary>Outcome of a <see cref="DeleteRoute"/>.</summary>
    public sealed record RouteDeleted(WebhookRouteName RouteName, bool Found)
        : IWebhookRouteResponse, INoSerializationVerificationNeeded;

    /// <summary>
    /// Outcome of a <see cref="GetRoute"/>. <paramref name="Found"/> reports
    /// whether the file exists; a found route with a null
    /// <paramref name="Route"/> is a file that exists but does not parse.
    /// </summary>
    public sealed record RouteResponse(WebhookRouteName RouteName, bool Found, WebhookRouteConfig? Route)
        : IWebhookRouteResponse, INoSerializationVerificationNeeded;

    /// <summary>
    /// One entry of a <see cref="RouteListResponse"/>. A null
    /// <paramref name="Definition"/> is a route file that does not parse.
    /// <para>
    /// The name stays a string here. This entry reports what the webhooks
    /// directory holds, and an operator can drop a file there whose name is not
    /// a valid route name. The list must show that file, not hide it.
    /// </para>
    /// </summary>
    public sealed record RouteEntry(string RouteName, WebhookRouteConfig? Definition);

    /// <summary>Outcome of a <see cref="ListRoutes"/>.</summary>
    public sealed record RouteListResponse(IReadOnlyList<RouteEntry> Routes)
        : IWebhookRouteResponse, INoSerializationVerificationNeeded;
}
