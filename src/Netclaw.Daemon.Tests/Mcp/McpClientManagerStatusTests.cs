// -----------------------------------------------------------------------
// <copyright file="McpClientManagerStatusTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using ModelContextProtocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpClientManagerStatusTests
{
    private static readonly DateTimeOffset ErrorAt = DateTimeOffset.Parse("2026-07-22T12:00:00Z");

    [Fact]
    public void BuildConnectionFailureStatus_WithoutTokensButWithOAuthChallenge_ReturnsAwaitingAuth()
    {
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            OAuthClientId = "client-id",
        };

        // A real OAuth challenge does not reach us as a bare transport 401/403: the MCP SDK
        // engages its OAuth handler on a Bearer WWW-Authenticate response and, when it cannot
        // complete non-interactively, throws this McpException. That is the only shape that
        // warrants "awaiting auth".
        var challenge = new McpException(
            "Failed to handle unauthorized response with 'Bearer' scheme. " +
            "The AuthorizationCallbackHandler returned a null authorization result.");

        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("notion"),
            entry,
            challenge,
            hasCachedTokens: false,
            hasOAuthRuntimeHints: true,
            ErrorAt);

        Assert.Equal(McpConnectionState.AwaitingAuth, status.State);
        Assert.Contains("netclaw mcp auth notion", status.ErrorMessage);
        Assert.Equal(ErrorAt, status.LastErrorAt);
    }

    [Fact]
    public void BuildConnectionFailureStatus_ForNonOAuth403_ReturnsTransportErrorNotAwaitingAuth()
    {
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
        };

        // A plain 403 with no OAuth challenge (no WWW-Authenticate: Bearer, no discoverable
        // protected-resource metadata) reaches us as a bare transport HttpRequestException --
        // e.g. a Host-header allowlist rejection. Even with OAuth runtime hints and no cached
        // tokens, this must surface as the real transport error, never "awaiting auth".
        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("playwright"),
            entry,
            new HttpRequestException(
                httpRequestError: HttpRequestError.Unknown,
                "Response status code does not indicate success: 403 (Forbidden). " +
                "Response body: Forbidden: Access is only allowed at localhost:8931",
                null,
                HttpStatusCode.Forbidden),
            hasCachedTokens: false,
            hasOAuthRuntimeHints: true,
            ErrorAt);

        Assert.NotEqual(McpConnectionState.AwaitingAuth, status.State);
        Assert.Equal(McpConnectionState.Unreachable, status.State);
        Assert.Contains("403 Forbidden", status.ErrorMessage);
        Assert.DoesNotContain("netclaw mcp auth", status.ErrorMessage);
        Assert.Equal(ErrorAt, status.LastErrorAt);
    }

    [Fact]
    public void BuildConnectionFailureStatus_WithTokensAndAuthFailure_ReturnsAuthFailed()
    {
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
        };

        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("notion"),
            entry,
            new HttpRequestException(httpRequestError: HttpRequestError.Unknown, "Forbidden", null, HttpStatusCode.Forbidden),
            hasCachedTokens: true,
            hasOAuthRuntimeHints: true,
            ErrorAt);

        Assert.Equal(McpConnectionState.AuthFailed, status.State);
        Assert.Contains("403 Forbidden", status.ErrorMessage);
        Assert.Contains("netclaw mcp auth notion", status.ErrorMessage);
        Assert.Equal(ErrorAt, status.LastErrorAt);
    }

    [Fact]
    public void BuildConnectionFailureStatus_ForNetworkFailure_ReturnsUnreachable()
    {
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
        };

        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("notion"),
            entry,
            new HttpRequestException("Connection refused"),
            hasCachedTokens: false,
            hasOAuthRuntimeHints: false,
            ErrorAt);

        Assert.Equal(McpConnectionState.Unreachable, status.State);
        Assert.Equal("Failed to reach MCP server. Check daemon logs for details.", status.ErrorMessage);
        Assert.DoesNotContain("Connection refused", status.ErrorMessage);
        Assert.Equal(ErrorAt, status.LastErrorAt);
    }

    [Fact]
    public void BuildConnectionFailureStatus_ForStdioSpawnFailureWithEmbeddedStatusLikeDigits_ReturnsUnreachable()
    {
        var entry = new McpServerEntry
        {
            Transport = "stdio",
            Command = "netclaw-missing-mcp-server-632401b4aa2f4c1e9c1b2a3d4e5f6789",
            Enabled = true,
        };

        // A stdio process-spawn failure carries the command name inside its message. The
        // command name is caller-supplied config data, not an HTTP signal, and can
        // coincidentally embed digits that look like a status code -- here "401" inside
        // the GUID suffix. No HTTP request ever occurs for stdio, so this must never be
        // misread as an HTTP 401 failure.
        var spawnFailure = new IOException(
            $"An error occurred trying to start process '{entry.Command}' with working " +
            "directory '/tmp'. No such file or directory");

        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("notifications"),
            entry,
            spawnFailure,
            hasCachedTokens: false,
            hasOAuthRuntimeHints: false,
            ErrorAt);

        Assert.Equal(McpConnectionState.Unreachable, status.State);
        Assert.Equal("Failed to reach MCP server. Check daemon logs for details.", status.ErrorMessage);
        Assert.DoesNotContain("401", status.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(ErrorAt, status.LastErrorAt);
    }

    [Fact]
    public void PublicErrorsNeverIncludeProviderBodySecrets()
    {
        const string providerBody = "code=oauth-code access_token=token-value client_secret=secret-value";
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
        };
        var exception = new HttpRequestException(
            HttpRequestError.Unknown,
            providerBody,
            null,
            HttpStatusCode.InternalServerError);

        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("notion"),
            entry,
            exception,
            hasCachedTokens: false,
            hasOAuthRuntimeHints: false,
            ErrorAt);
        var oauthError = McpClientManager.CreateSafeOAuthError(exception, "connection initialization");

        Assert.Equal("MCP server request failed (HTTP 500 InternalServerError).", status.ErrorMessage);
        Assert.DoesNotContain("oauth-code", status.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", oauthError.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", oauthError.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void WrappedRetiredCredentialWriterIsClassifiedAsCredentialPersistence()
    {
        var exception = new InvalidOperationException(
            "SDK token cache callback failed.",
            new McpOAuthRetiredCredentialWriterException("The prior connection no longer owns credentials."));

        var error = McpClientManager.CreateSafeOAuthError(exception, "connection initialization");

        Assert.Equal("credential persistence", error.Operation);
        Assert.Equal(
            "MCP OAuth credential persistence failed. Check daemon logs for details.",
            error.Error);
        Assert.DoesNotContain("prior connection", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicRegistrationBadRequestIsNotMisreportedAsOuterUnauthorizedChallenge()
    {
        var exception = new InvalidOperationException(
            "Failed to handle unauthorized response with 'Bearer' scheme. " +
            "Dynamic client registration failed with status BadRequest: invalid_client_metadata");

        var error = McpClientManager.CreateSafeOAuthError(exception, "connection initialization");

        Assert.Equal("dynamic client registration", error.Operation);
        Assert.Equal(400, error.Status);
        Assert.Contains("HTTP 400 BadRequest", error.Error, StringComparison.Ordinal);
    }
}
