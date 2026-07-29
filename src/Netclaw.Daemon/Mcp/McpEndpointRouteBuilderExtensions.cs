// -----------------------------------------------------------------------
// <copyright file="McpEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http;
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
public sealed record McpServerStatusDto(
    string State,
    int ToolCount,
    string? Error,
    DateTimeOffset? LastErrorAt);

/// <summary>OAuth flow status for an MCP server or pending state token.</summary>
public sealed record McpOAuthStatusResponse(string Status, McpErrorResponse? Error = null);

/// <summary>Error payload returned when an MCP request is malformed or unknown.</summary>
public sealed record McpErrorResponse(
    string Error,
    string? Operation = null,
    int? Status = null);

public static class McpEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        // MCP OAuth 2.1 endpoints
        app.MapPost("/api/mcp/oauth/start/{name}", async Task<IResult> (
            string name,
            McpClientManager mcpManager,
            Dictionary<string, McpServerEntry> mcpServers,
            ILogger<McpClientManager> logger,
            CancellationToken requestCancellation) =>
        {
            if (!mcpServers.TryGetValue(name, out var entry))
                return Results.NotFound(new McpErrorResponse($"MCP server '{name}' not found."));

            if (string.IsNullOrWhiteSpace(entry.Url))
                return Results.BadRequest(new McpErrorResponse(
                    $"MCP server '{name}' has no URL (OAuth requires HTTP transport)."));

            try
            {
                var started = await mcpManager.StartAuthorizationAsync(
                    new McpServerName(name),
                    requestCancellation);
                return Results.Ok(started);
            }
            // A disconnected client is not a server error. Without this clause the
            // catch-all below would map the aborted request onto an error response and
            // log it as a failure. The filter keeps genuine timeouts, which arrive as
            // TaskCanceledException while the request token is still live, classified as
            // faults rather than as the caller hanging up.
            // Same guard as the start endpoint: a client that hung up must not be
            // reported as a callback failure.
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (McpOAuthOperationException ex)
            {
                logger.LogError(ex, "MCP OAuth start failed for server '{Name}'", name);
                return Results.Json(ex.Error, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MCP OAuth start failed for server '{Name}'", name);
                var error = McpClientManager.CreateSafeOAuthError(ex, "authorization start");
                return Results.Json(error, statusCode: StatusCodes.Status502BadGateway);
            }
        })
        .WithName("StartMcpOAuth")
        .WithSummary("Start an OAuth 2.1 authorization flow for an MCP server.")
        .WithTags("MCP")
        .RequireAuthorization();

        app.MapGet("/api/mcp/oauth/callback", async Task<IResult> (
            [AsParameters] McpOAuthCallbackQuery query,
            McpOAuthFlowBroker flowBroker,
            ILogger<McpOAuthFlowBroker> logger,
            CancellationToken requestCancellation) =>
        {
            if (string.IsNullOrEmpty(query.Code) || string.IsNullOrEmpty(query.State))
            {
                return TypedResults.Content(
                    "<html><body><h2>Authorization failed</h2><p>Missing code or state parameter.</p></body></html>",
                    "text/html",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var flow = flowBroker.GetForCallback(query.State);
                flow.DeliverCode(query.Code);
                var terminal = await flow.WaitForTerminalAsync(requestCancellation);
                if (terminal.Status is McpOAuthFlowStatus.Failed)
                {
                    var message = terminal.Error?.Error
                                  ?? "Authorization failed. Check daemon logs and start a new attempt.";
                    return TypedResults.Content(
                        $"<html><body><h2>Authorization failed</h2><p>{System.Net.WebUtility.HtmlEncode(message)}</p></body></html>",
                        "text/html",
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                return TypedResults.Content(
                    "<html><body><h2>Authorization complete</h2><p>You may close this tab.</p></body></html>",
                    "text/html");
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (McpOAuthCallbackException ex)
            {
                logger.LogWarning("Rejected MCP OAuth callback: {Reason}", ex.Message);
                return TypedResults.Content(
                    $"<html><body><h2>Authorization failed</h2><p>{System.Net.WebUtility.HtmlEncode(ex.Message)}</p></body></html>",
                    "text/html",
                    contentEncoding: null,
                    statusCode: StatusCodes.Status400BadRequest);
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
                    kvp.Value.ErrorMessage,
                    kvp.Value.LastErrorAt));
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

        app.MapGet("/api/mcp/oauth/status/{name}", (string name, McpOAuthFlowBroker flowBroker) =>
        {
            var terminal = flowBroker.GetLatestStatus(new McpServerName(name));
            return TypedResults.Ok(new McpOAuthStatusResponse(
                terminal.Status.ToString(),
                terminal.Error));
        })
        .WithName("GetMcpOAuthStatus")
        .WithSummary("Get the OAuth flow status for an MCP server by name.")
        .WithTags("MCP")
        .RequireAuthorization();

        app.MapGet("/api/mcp/oauth/status-by-state/{state}", (string state, McpOAuthFlowBroker flowBroker) =>
        {
            var terminal = flowBroker.GetStatusByState(state);
            // Tokens are persisted daemon-side — never expose them over HTTP.
            return TypedResults.Ok(new McpOAuthStatusResponse(
                terminal.Status.ToString(),
                terminal.Error));
        })
        .WithName("GetMcpOAuthStatusByState")
        .WithSummary("Get the OAuth flow status for an MCP server by state token.")
        .WithTags("MCP")
        .RequireAuthorization();

        return app;
    }
}
