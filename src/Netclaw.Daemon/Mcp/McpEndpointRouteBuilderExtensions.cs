// -----------------------------------------------------------------------
// <copyright file="McpEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

/// <summary>Query string for the MCP OAuth browser callback.</summary>
public sealed record McpOAuthCallbackQuery(
    [FromQuery(Name = "code")] string? Code,
    [FromQuery(Name = "state")] string? State);

/// <summary>Authorization URL and opaque state returned when an MCP OAuth flow starts.</summary>
public sealed record McpOAuthStartResponse(string AuthorizationUrl, string State);

/// <summary>Connection status for a single MCP server.</summary>
public sealed record McpServerStatusDto(string State, int ToolCount, string? Error);

/// <summary>OAuth flow status for an MCP server or pending state token.</summary>
public sealed record McpOAuthStatusResponse(string Status);

/// <summary>Error payload returned when an MCP request is malformed or unknown.</summary>
public sealed record McpErrorResponse(string Error);

public static class McpEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        // MCP OAuth 2.1 endpoints
        app.MapPost("/api/mcp/oauth/start/{name}", async ValueTask<Results<Ok<McpOAuthStartResponse>, NotFound<McpErrorResponse>, BadRequest<McpErrorResponse>>> (
            string name,
            McpOAuthService oauthService,
            Dictionary<string, McpServerEntry> mcpServers,
            CancellationToken ct) =>
        {
            if (!mcpServers.TryGetValue(name, out var entry))
                return TypedResults.NotFound(new McpErrorResponse($"MCP server '{name}' not found"));

            if (string.IsNullOrWhiteSpace(entry.Url))
                return TypedResults.BadRequest(new McpErrorResponse($"MCP server '{name}' has no URL (OAuth requires HTTP transport)"));

            var (authUrl, state) = await oauthService.StartAuthorizationFlowAsync(new McpServerName(name), entry, ct);
            return TypedResults.Ok(new McpOAuthStartResponse(authUrl, state));
        })
        .WithName("StartMcpOAuth")
        .WithSummary("Start an OAuth 2.1 authorization flow for an MCP server.")
        .WithTags("MCP")
        .RequireAuthorization();

        app.MapGet("/api/mcp/oauth/callback", async ValueTask<ContentHttpResult> (
            [AsParameters] McpOAuthCallbackQuery query,
            McpOAuthService oauthService,
            IMcpReconnectable mcpManager,
            ILogger<McpClientManager> reconnectLogger,
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
                await oauthService.CompleteAuthorizationAsync(query.Code, query.State, ct);

                // Auto-reconnect the MCP server now that we have a valid token.
                // Resolved as IMcpReconnectable so the callback is not coupled to the
                // concrete McpClientManager type, which is heavyweight and hard to stub in tests.
                var serverName = oauthService.GetServerNameForState(query.State);
                if (serverName is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await mcpManager.TryReconnectAsync(serverName.Value, CancellationToken.None); }
                        catch (Exception ex) { reconnectLogger.LogWarning(ex, "Post-OAuth reconnect failed for MCP server '{Name}'", serverName.Value.Value); }
                    }, CancellationToken.None);
                }

                return TypedResults.Content(
                    "<html><body><h2>Authorization complete</h2><p>You may close this tab.</p></body></html>",
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
        .WithName("McpOAuthCallback")
        .WithSummary("Browser redirect callback that completes an MCP OAuth flow.")
        .WithTags("MCP")
        .AllowAnonymous();

        app.MapGet("/api/mcp/statuses", (McpClientManager mcpManager) =>
        {
            var statuses = mcpManager.GetServerStatuses();
            var result = statuses.ToDictionary(
                kvp => kvp.Key.Value,
                kvp => new McpServerStatusDto(
                    kvp.Value.State.ToString(),
                    kvp.Value.ToolCount,
                    kvp.Value.ErrorMessage));
            return TypedResults.Ok(result);
        })
        .WithName("GetMcpServerStatuses")
        .WithSummary("Get the connection status of all configured MCP servers.")
        .WithTags("MCP")
        .RequireAuthorization();

        app.MapGet("/api/mcp/tools/{name}", (string name, McpClientManager mcpManager) =>
        {
            var tools = mcpManager.GetToolNames(new McpServerName(name));
            return TypedResults.Ok(tools);
        })
        .WithName("GetMcpServerTools")
        .WithSummary("List the tool names exposed by a single MCP server.")
        .WithTags("MCP")
        .RequireAuthorization();

        app.MapGet("/api/mcp/oauth/status/{name}", (string name, McpOAuthService oauthService) =>
        {
            var status = oauthService.GetFlowStatus(new McpServerName(name));
            return TypedResults.Ok(new McpOAuthStatusResponse(status.ToString()));
        })
        .WithName("GetMcpOAuthStatus")
        .WithSummary("Get the OAuth flow status for an MCP server by name.")
        .WithTags("MCP")
        .RequireAuthorization();

        app.MapGet("/api/mcp/oauth/status-by-state/{state}", (string state, McpOAuthService oauthService) =>
        {
            var status = oauthService.GetFlowStatusByState(state);
            // Tokens are persisted daemon-side — never expose them over HTTP.
            return TypedResults.Ok(new McpOAuthStatusResponse(status.ToString()));
        })
        .WithName("GetMcpOAuthStatusByState")
        .WithSummary("Get the OAuth flow status for an MCP server by state token.")
        .WithTags("MCP")
        .RequireAuthorization();

        return app;
    }
}
