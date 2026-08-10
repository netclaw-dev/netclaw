// -----------------------------------------------------------------------
// <copyright file="McpClientManagerStatusTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpClientManagerStatusTests
{
    private static readonly DateTimeOffset ErrorAt = DateTimeOffset.Parse("2026-07-22T12:00:00Z");

    [Fact]
    public void BuildConnectionFailureStatus_WithoutTokensButWithOAuthHints_ReturnsAwaitingAuth()
    {
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            OAuthClientId = "client-id",
        };

        var status = McpClientManager.BuildConnectionFailureStatus(
            new McpServerName("notion"),
            entry,
            new HttpRequestException(httpRequestError: HttpRequestError.Unknown, "Unauthorized", null, HttpStatusCode.Unauthorized),
            hasCachedTokens: false,
            hasOAuthRuntimeHints: true,
            ErrorAt);

        Assert.Equal(McpConnectionState.AwaitingAuth, status.State);
        Assert.Contains("netclaw mcp auth notion", status.ErrorMessage);
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
