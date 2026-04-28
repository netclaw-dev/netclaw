// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Providers;
using Netclaw.Providers.OAuth;

namespace Netclaw.Daemon.Providers;

internal static class ProviderOAuthEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapProviderOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/provider/oauth/start", (
            HttpContext context,
            OAuthPkceService pkceService,
            ProviderDescriptorRegistry registry,
            IProviderOAuthCallbackListener callbackListener) =>
        {
            var providerType = context.Request.Query["provider"].ToString();
            if (string.IsNullOrEmpty(providerType))
                return Results.BadRequest(new { error = "Missing 'provider' query parameter" });

            if (!registry.TryGet(providerType, out var descriptor))
                return Results.NotFound(new { error = $"Unknown provider type: {providerType}" });

            var oauth = descriptor.Auth.GetOAuthConfig();
            if (oauth is null || oauth.AuthorizationEndpoint is null || oauth.RedirectUri is null)
                return Results.BadRequest(new { error = $"Provider '{providerType}' does not support browser OAuth" });

            var (authUrl, state) = pkceService.StartAuthorizationFlow(
                oauth.AuthorizationEndpoint.AbsoluteUri,
                oauth.TokenEndpoint.AbsoluteUri,
                oauth.ClientId,
                oauth.RedirectUri.AbsoluteUri,
                oauth.Scope,
                oauth.ExtraAuthParams);

            callbackListener.StartListening(oauth.RedirectUri.AbsoluteUri, state);

            return Results.Ok(new { authorizationUrl = authUrl, state });
        }).RequireAuthorization();

        app.MapGet("/api/provider/oauth/callback", async (
            HttpContext context,
            OAuthPkceService pkceService,
            CancellationToken ct) =>
        {
            var code = context.Request.Query["code"].ToString();
            var state = context.Request.Query["state"].ToString();

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(
                    "<html><body><h2>Authorization failed</h2><p>Missing code or state parameter.</p></body></html>", ct);
                return;
            }

            try
            {
                await pkceService.CompleteAuthorizationAsync(code, state, ct);
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(
                    "<html><body><h2>Authorization complete</h2><p>You may close this tab and return to the terminal.</p></body></html>", ct);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(
                    $"<html><body><h2>Authorization failed</h2><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>", ct);
            }
        }).AllowAnonymous();

        app.MapGet("/api/provider/oauth/status/{state}", (
            string state,
            OAuthPkceService pkceService) =>
        {
            var status = pkceService.GetFlowStatus(state);
            var result = pkceService.GetFlowResult(state);
            return Results.Ok(new
            {
                status = status.ToString(),
                hasToken = result is not null,
                accessToken = result?.AccessToken.Value,
                refreshToken = result?.RefreshToken?.Value,
                expiresAt = result?.ExpiresAt?.ToString("o"),
            });
        }).RequireAuthorization();

        return app;
    }
}
