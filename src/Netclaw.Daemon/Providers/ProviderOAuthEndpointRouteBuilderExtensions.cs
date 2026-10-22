// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Security;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;

namespace Netclaw.Daemon.Providers;

/// <summary>Query string identifying which provider's OAuth flow to start.</summary>
internal sealed record ProviderOAuthStartQuery([FromQuery(Name = "provider")] string? Provider);

/// <summary>Query string for the provider OAuth browser callback.</summary>
internal sealed record ProviderOAuthCallbackQuery(
    [FromQuery(Name = "code")] string? Code,
    [FromQuery(Name = "state")] string? State);

/// <summary>Authorization URL and opaque state returned when a provider OAuth flow starts.</summary>
internal sealed record ProviderOAuthStartResponse(string AuthorizationUrl, string State);

/// <summary>
/// Status of a provider OAuth flow. It never carries tokens. The host CLI reads the
/// tokens from <c>POST /api/local-control/v1/provider-oauth-result</c> with a
/// local-control proof.
/// </summary>
internal sealed record ProviderOAuthStatusResponse(
    string Status,
    bool HasToken,
    string? ExpiresAt);

/// <summary>Tokens of a completed provider OAuth flow, returned only for a valid local-control proof.</summary>
internal sealed record ProviderOAuthResultResponse(
    string AccessToken,
    string? RefreshToken,
    string? AccountId,
    string? ExpiresAt);

/// <summary>Error payload returned when a provider OAuth request is malformed or unknown.</summary>
internal sealed record ProviderOAuthErrorResponse(string Error);

internal static class ProviderOAuthEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapProviderOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/provider/oauth/start", Results<Ok<ProviderOAuthStartResponse>, BadRequest<ProviderOAuthErrorResponse>, NotFound<ProviderOAuthErrorResponse>> (
            [AsParameters] ProviderOAuthStartQuery query,
            OAuthPkceService pkceService,
            ProviderDescriptorRegistry registry,
            IProviderOAuthCallbackListener callbackListener) =>
        {
            if (string.IsNullOrEmpty(query.Provider))
                return TypedResults.BadRequest(new ProviderOAuthErrorResponse("Missing 'provider' query parameter"));

            if (!registry.TryGet(query.Provider, out var descriptor))
                return TypedResults.NotFound(new ProviderOAuthErrorResponse($"Unknown provider type: {query.Provider}"));

            var oauth = descriptor.Auth.GetOAuthConfig();
            if (oauth is null || oauth.AuthorizationEndpoint is null || oauth.RedirectUri is null)
                return TypedResults.BadRequest(new ProviderOAuthErrorResponse($"Provider '{query.Provider}' does not support browser OAuth"));

            var (authUrl, state) = pkceService.StartAuthorizationFlow(
                oauth.AuthorizationEndpoint.AbsoluteUri,
                oauth.TokenEndpoint.AbsoluteUri,
                oauth.ClientId,
                oauth.RedirectUri.AbsoluteUri,
                oauth.Scope,
                oauth.ExtraAuthParams);

            callbackListener.StartListening(oauth.RedirectUri.AbsoluteUri, state);

            return TypedResults.Ok(new ProviderOAuthStartResponse(authUrl, state));
        })
        .WithName("StartProviderOAuth")
        .WithSummary("Start a browser OAuth authorization flow for a model provider.")
        .WithTags("Provider OAuth")
        .RequireAuthorization();

        app.MapGet("/api/provider/oauth/callback", async ValueTask<ContentHttpResult> (
            [AsParameters] ProviderOAuthCallbackQuery query,
            OAuthPkceService pkceService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(query.Code) || string.IsNullOrEmpty(query.State))
            {
                return TypedResults.Content(
                    "<html><body><h2>Authorization failed</h2><p>Missing code or state parameter.</p></body></html>",
                    "text/html");
            }

            try
            {
                await pkceService.CompleteAuthorizationAsync(query.Code, query.State, ct);
                return TypedResults.Content(
                    "<html><body><h2>Authorization complete</h2><p>You may close this tab and return to the terminal.</p></body></html>",
                    "text/html");
            }
            catch (Exception ex)
            {
                return TypedResults.Content(
                    $"<html><body><h2>Authorization failed</h2><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>",
                    "text/html",
                    contentEncoding: null,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("ProviderOAuthCallback")
        .WithSummary("Browser redirect callback that completes a provider OAuth flow.")
        .WithTags("Provider OAuth")
        .AllowAnonymous();

        app.MapGet("/api/provider/oauth/status/{state}", (
            string state,
            OAuthPkceService pkceService) =>
        {
            var status = pkceService.GetFlowStatus(state);
            var result = pkceService.GetFlowResult(state);

            return TypedResults.Ok(new ProviderOAuthStatusResponse(
                Status: status.ToString(),
                HasToken: result is not null,
                ExpiresAt: result?.ExpiresAt?.ToString("o")));
        })
        .WithName("GetProviderOAuthStatus")
        .WithSummary("Get the status of a provider OAuth flow. The response never contains tokens.")
        .WithTags("Provider OAuth")
        .RequireAuthorization();

        app.MapPost("/api/local-control/v1/provider-oauth-result", ReadProviderOAuthResultAsync)
            .WithName("ReadProviderOAuthResultV1")
            .WithSummary("Read the tokens of a completed provider OAuth flow with a daemon-host local-control proof.")
            .WithTags("Provider OAuth")
            .AllowAnonymous();

        return app;
    }

    // Authority comes only from a local-control proof bound to this OAuth state. A
    // tunnel agent connects from loopback and a paired device holds a bearer token, so
    // neither the socket address nor a device or bootstrap token can unlock the tokens.
    private static async Task<IResult> ReadProviderOAuthResultAsync(
        HttpContext httpContext,
        LocalControlRequests localControl,
        OAuthPkceService pkceService,
        CancellationToken cancellationToken)
    {
        var (request, refusal) = await localControl.AuthorizeAsync(
            httpContext,
            PairingEndpointJsonContext.Default.LocalControlProviderOAuthResultRequest,
            cancellationToken);
        if (refusal is not null)
            return refusal;

        var result = pkceService.GetFlowResult(request!.State);
        if (result is null)
            return TypedResults.Conflict(new ProviderOAuthErrorResponse("The provider OAuth flow has no completed result."));

        return TypedResults.Ok(new ProviderOAuthResultResponse(
            AccessToken: result.AccessToken.Value,
            RefreshToken: result.RefreshToken?.Value,
            AccountId: result.AccountId?.Value,
            ExpiresAt: result.ExpiresAt?.ToString("o")));
    }
}
