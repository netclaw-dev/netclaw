// -----------------------------------------------------------------------
// <copyright file="McpEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

public static class McpEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        // MCP OAuth 2.1 endpoints
        app.MapPost("/api/mcp/oauth/start/{name}", async (
            string name,
            McpOAuthService oauthService,
            Dictionary<string, McpServerEntry> mcpServers,
            CancellationToken ct) =>
        {
            if (!mcpServers.TryGetValue(name, out var entry))
                return Results.NotFound(new { error = $"MCP server '{name}' not found" });

            if (string.IsNullOrWhiteSpace(entry.Url))
                return Results.BadRequest(new { error = $"MCP server '{name}' has no URL (OAuth requires HTTP transport)" });

            var (authUrl, state) = await oauthService.StartAuthorizationFlowAsync(new McpServerName(name), entry, ct);
            return Results.Ok(new { authorizationUrl = authUrl, state });
        }).RequireAuthorization();

        app.MapGet("/api/mcp/oauth/callback", async (
            HttpContext context,
            McpOAuthService oauthService,
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
                await oauthService.CompleteAuthorizationAsync(code, state, ct);

                // Auto-reconnect the MCP server now that we have a valid token.
                // Resolved as IMcpReconnectable so the callback is not coupled to the
                // concrete McpClientManager type, which is heavyweight and hard to stub in tests.
                var serverName = oauthService.GetServerNameForState(state);
                if (serverName is not null)
                {
                    var mcpManager = context.RequestServices.GetRequiredService<IMcpReconnectable>();
                    var reconnectLogger = context.RequestServices.GetRequiredService<ILogger<McpClientManager>>();
                    _ = Task.Run(async () =>
                    {
                        try { await mcpManager.TryReconnectAsync(serverName.Value, CancellationToken.None); }
                        catch (Exception ex) { reconnectLogger.LogWarning(ex, "Post-OAuth reconnect failed for MCP server '{Name}'", serverName.Value.Value); }
                    }, CancellationToken.None);
                }

                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(
                    "<html><body><h2>Authorization complete</h2><p>You may close this tab.</p></body></html>", ct);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(
                    $"<html><body><h2>Authorization failed</h2><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>", ct);
            }
        }).AllowAnonymous();

        app.MapGet("/api/mcp/statuses", (McpClientManager mcpManager) =>
        {
            var statuses = mcpManager.GetServerStatuses();
            var result = statuses.ToDictionary(
                kvp => kvp.Key.Value,
                kvp => new
                {
                    state = kvp.Value.State.ToString(),
                    toolCount = kvp.Value.ToolCount,
                    error = kvp.Value.ErrorMessage,
                });
            return Results.Ok(result);
        }).RequireAuthorization();

        app.MapGet("/api/mcp/tools/{name}", (string name, McpClientManager mcpManager) =>
        {
            var tools = mcpManager.GetToolNames(new McpServerName(name));
            return Results.Ok(tools);
        }).RequireAuthorization();

        app.MapGet("/api/mcp/oauth/status/{name}", (string name, McpOAuthService oauthService) =>
        {
            var status = oauthService.GetFlowStatus(new McpServerName(name));
            return Results.Ok(new { status = status.ToString() });
        }).RequireAuthorization();

        app.MapGet("/api/mcp/oauth/status-by-state/{state}", (string state, McpOAuthService oauthService) =>
        {
            var status = oauthService.GetFlowStatusByState(state);
            // Tokens are persisted daemon-side — never expose them over HTTP.
            return Results.Ok(new { status = status.ToString() });
        }).RequireAuthorization();

        return app;
    }
}
