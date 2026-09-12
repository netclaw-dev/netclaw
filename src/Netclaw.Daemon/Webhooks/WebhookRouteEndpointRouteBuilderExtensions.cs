// -----------------------------------------------------------------------
// <copyright file="WebhookRouteEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Configuration;
using static Netclaw.Actors.Webhooks.WebhookRouteProtocol;

namespace Netclaw.Daemon.Webhooks;

/// <summary>
/// Webhook route management resource. Every handler is a thin front over
/// <c>WebhookRouteActor</c>, the single mutation authority — validation and the
/// audience authority check live in the actor, never here.
/// <para>
/// The resource is additive. It shares the <c>/api/webhooks</c> prefix with the
/// anonymous delivery endpoint (<c>POST /api/webhooks/{route}</c>) but claims
/// only the GET, PUT, and DELETE methods, so delivery is untouched.
/// </para>
/// </summary>
public static class WebhookRouteEndpointRouteBuilderExtensions
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(10);

    public static IEndpointRouteBuilder MapWebhookRouteEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/webhooks")
            .WithTags("Webhooks")
            .RequireAuthorization();

        routes.MapGet("", async ValueTask<Ok<IEnumerable<WebhookRouteSummaryDto>>> (
            IRequiredActor<WebhookRouteActorKey> actor,
            CancellationToken ct) =>
        {
            var routeActor = await actor.GetAsync(ct);
            var response = await routeActor.Ask<RouteListResponse>(ListRoutes.Instance, AskTimeout, ct);
            return TypedResults.Ok(response.Routes.Select(ToSummary));
        })
        .WithName("ListWebhookRoutes")
        .WithSummary("List configured inbound webhook routes.");

        routes.MapGet("/{name}", async ValueTask<Results<Ok<WebhookRouteDto>, NotFound<WebhookRouteErrorResponse>, ProblemHttpResult>> (
            string name,
            IRequiredActor<WebhookRouteActorKey> actor,
            CancellationToken ct) =>
        {
            // A name the value object refuses can never name a stored file, so the
            // caller gets the same answer as a missing route.
            if (!WebhookRouteName.TryCreate(name, out var routeName, out _))
                return TypedResults.NotFound(new WebhookRouteErrorResponse($"Webhook route '{name}' not found."));

            var routeActor = await actor.GetAsync(ct);
            var response = await routeActor.Ask<RouteResponse>(new GetRoute(routeName), AskTimeout, ct);

            if (!response.Found)
                return TypedResults.NotFound(new WebhookRouteErrorResponse($"Webhook route '{name}' not found."));

            // The file exists but does not parse. Report it loudly instead of
            // pretending the route is absent — a corrupt route file fails
            // delivery closed and the operator needs to see that.
            if (response.Route is null)
                return TypedResults.Problem(
                    detail: $"Webhook route '{response.RouteName.Value}' exists but could not be parsed.",
                    statusCode: StatusCodes.Status500InternalServerError);

            return TypedResults.Ok(ToDto(response.RouteName.Value, response.Route));
        })
        .WithName("GetWebhookRoute")
        .WithSummary("Get one webhook route. The response never carries the route secret.");

        routes.MapPut("/{name}", async ValueTask<Results<Ok<WebhookRouteDto>, BadRequest<WebhookRouteErrorResponse>, ProblemHttpResult>> (
            string name,
            UpsertWebhookRouteRequest request,
            IRequiredActor<WebhookRouteActorKey> actor,
            ClaimsPrincipalMapper mapper,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Creating or updating a route requires Operator authority, mirroring
            // POST /api/reminders. Without it there is no audience to attribute
            // the route to, and defaulting one would mint authority silently.
            if (ResolveCreatorAudience(mapper, httpContext) is not { } creatorAudience)
                return TypedResults.Problem(
                    detail: "Writing a webhook route requires Operator authority.",
                    statusCode: StatusCodes.Status403Forbidden);

            if (!WebhookRouteName.TryCreate(name, out var routeName, out var nameError))
                return TypedResults.BadRequest(new WebhookRouteErrorResponse(nameError!));

            if (!TryBuildUpsert(routeName, request, creatorAudience, out var command, out var requestError))
                return TypedResults.BadRequest(new WebhookRouteErrorResponse(requestError!));

            var routeActor = await actor.GetAsync(ct);
            var response = await routeActor.Ask<RouteSaved>(command!, AskTimeout, ct);

            return response.Outcome switch
            {
                RouteSaveOutcome.Created or RouteSaveOutcome.Updated =>
                    TypedResults.Ok(ToDto(response.RouteName.Value, response.Route!)),
                RouteSaveOutcome.AuthorityRejected => TypedResults.Problem(
                    detail: response.ErrorMessage,
                    statusCode: StatusCodes.Status403Forbidden),
                _ => TypedResults.BadRequest(
                    new WebhookRouteErrorResponse(response.ErrorMessage ?? "Webhook route rejected."))
            };
        })
        .WithName("UpsertWebhookRoute")
        .WithSummary("Create or update a webhook route. Omitted fields keep their stored values.");

        routes.MapDelete("/{name}", async ValueTask<Results<NoContent, NotFound<WebhookRouteErrorResponse>>> (
            string name,
            IRequiredActor<WebhookRouteActorKey> actor,
            CancellationToken ct) =>
        {
            if (!WebhookRouteName.TryCreate(name, out var routeName, out _))
                return TypedResults.NotFound(new WebhookRouteErrorResponse($"Webhook route '{name}' not found."));

            var routeActor = await actor.GetAsync(ct);
            var response = await routeActor.Ask<RouteDeleted>(new DeleteRoute(routeName), AskTimeout, ct);

            return response.Found
                ? TypedResults.NoContent()
                : TypedResults.NotFound(new WebhookRouteErrorResponse($"Webhook route '{name}' not found."));
        })
        .WithName("DeleteWebhookRoute")
        .WithSummary("Delete a webhook route.");

        return app;
    }

    /// <summary>
    /// Maps the caller to the authority the route is created under. Only an
    /// Operator carries one; every other principal gets null and is refused.
    /// </summary>
    private static TrustAudience? ResolveCreatorAudience(ClaimsPrincipalMapper mapper, HttpContext httpContext)
    {
        var identity = mapper.Map(httpContext.User);
        return identity.Principal is PrincipalClassification.Operator
            ? TrustAudience.Personal
            : null;
    }

    /// <summary>
    /// Converts the request body's wire spellings into the actor's field-level
    /// patch. A null property in the body means "leave the stored value
    /// unchanged", the same rule the agent tool and the CLI already use.
    /// </summary>
    private static bool TryBuildUpsert(
        WebhookRouteName routeName,
        UpsertWebhookRouteRequest request,
        TrustAudience creatorAudience,
        out UpsertRoute? command,
        out string? error)
    {
        command = null;
        error = null;

        WebhookVerifierKind? verificationKind = null;
        if (request.VerificationKind is not null)
        {
            if (!WebhookRouteValidator.TryParseVerifierKind(request.VerificationKind, out var parsedKind))
            {
                error = "'verificationKind' must be 'Hmac', 'HmacTimestamped', or 'HeaderSecret'.";
                return false;
            }

            verificationKind = parsedKind;
        }

        TrustAudience? requestedAudience = null;
        if (request.Audience is not null)
        {
            if (!SecurityPolicyDefaults.TryParseAudience(request.Audience, out var parsedAudience))
            {
                error = "'audience' must be Public, Team, or Personal.";
                return false;
            }

            requestedAudience = parsedAudience;
        }

        command = new UpsertRoute
        {
            RouteName = routeName,
            CreatorAudience = creatorAudience,
            RequestedAudience = requestedAudience,
            VerificationKind = verificationKind,
            Prompt = request.Prompt,
            Secret = request.Secret,
            Events = request.Events,
            NotifyInstructions = request.NotifyInstructions,
            DeliveryRequired = request.DeliveryRequired,
            NotificationChannelId = request.NotificationChannelId,
            MaxBodyBytes = request.MaxBodyBytes,
            RateLimitPerMinute = request.RateLimitPerMinute,
            Enabled = request.Enabled,
            SignatureHeaderName = request.SignatureHeaderName,
            SignaturePrefix = request.SignaturePrefix,
            SecretHeaderName = request.SecretHeaderName,
            EventHeaderName = request.EventHeaderName,
            DeliveryIdHeaderName = request.DeliveryIdHeaderName,
            TimestampField = request.TimestampField,
            SignatureField = request.SignatureField,
            SignedPayloadSeparator = request.SignedPayloadSeparator,
            ToleranceSeconds = request.ToleranceSeconds
        };
        return true;
    }

    private static WebhookRouteSummaryDto ToSummary(RouteEntry entry) => new(
        Name: entry.RouteName,
        Valid: entry.Definition is not null,
        Enabled: entry.Definition?.Enabled,
        Audience: entry.Definition?.Audience.ToWireValue(),
        VerificationKind: entry.Definition?.Verification.Kind.ToString(),
        DeliveryRequired: entry.Definition?.DeliveryRequired);

    /// <summary>
    /// Projects a stored route for an HTTP response. The verification secret is
    /// never projected: route files are secret-bearing config and the resource
    /// is a management surface, not a secret-read surface.
    /// </summary>
    private static WebhookRouteDto ToDto(string routeName, WebhookRouteConfig route) => new(
        Name: routeName,
        Enabled: route.Enabled,
        Audience: route.Audience.ToWireValue(),
        Prompt: route.Prompt,
        Events: route.Events,
        NotifyInstructions: route.NotifyInstructions,
        DeliveryRequired: route.DeliveryRequired,
        NotificationChannelId: route.NotificationTarget?.ChannelId,
        MaxBodyBytes: route.MaxBodyBytes,
        RateLimitPerMinute: route.RateLimitPerMinute,
        Verification: new WebhookVerificationDto(
            Kind: route.Verification.Kind.ToString(),
            HmacAlgorithm: route.Verification.HmacAlgorithm.ToString(),
            SignatureHeaderName: route.Verification.SignatureHeaderName,
            SignaturePrefix: route.Verification.SignaturePrefix,
            SecretHeaderName: route.Verification.SecretHeaderName,
            EventHeaderName: route.Verification.EventHeaderName,
            DeliveryIdHeaderName: route.Verification.DeliveryIdHeaderName,
            TimestampField: route.Verification.TimestampField,
            SignatureField: route.Verification.SignatureField,
            SignedPayloadSeparator: route.Verification.SignedPayloadSeparator,
            ToleranceSeconds: route.Verification.ToleranceSeconds));
}

/// <summary>
/// Request body for <c>PUT /api/webhooks/{name}</c>. Every property is optional:
/// an omitted property leaves the stored value unchanged.
/// </summary>
internal sealed record UpsertWebhookRouteRequest
{
    public string? Prompt { get; init; }
    public string? Secret { get; init; }
    public string? VerificationKind { get; init; }
    public string? Audience { get; init; }
    public IReadOnlyList<string>? Events { get; init; }
    public string? NotifyInstructions { get; init; }
    public bool? DeliveryRequired { get; init; }
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

/// <summary>Summary projection returned by <c>GET /api/webhooks</c>.</summary>
internal sealed record WebhookRouteSummaryDto(
    string Name,
    bool Valid,
    bool? Enabled,
    string? Audience,
    string? VerificationKind,
    bool? DeliveryRequired);

/// <summary>Full route projection, without the verification secret.</summary>
internal sealed record WebhookRouteDto(
    string Name,
    bool Enabled,
    string Audience,
    string Prompt,
    IReadOnlyList<string> Events,
    string NotifyInstructions,
    bool DeliveryRequired,
    string? NotificationChannelId,
    int MaxBodyBytes,
    int RateLimitPerMinute,
    WebhookVerificationDto Verification);

/// <summary>Verification settings projection, without the secret.</summary>
internal sealed record WebhookVerificationDto(
    string Kind,
    string HmacAlgorithm,
    string? SignatureHeaderName,
    string? SignaturePrefix,
    string? SecretHeaderName,
    string? EventHeaderName,
    string? DeliveryIdHeaderName,
    string? TimestampField,
    string? SignatureField,
    string? SignedPayloadSeparator,
    int? ToleranceSeconds);

/// <summary>Error payload returned when a webhook route request fails.</summary>
internal sealed record WebhookRouteErrorResponse(string Error);
