// -----------------------------------------------------------------------
// <copyright file="OAuthClientRejectionHandlerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Configuration.Http;
using Xunit;

namespace Netclaw.Configuration.Tests.Http;

public sealed class OAuthClientRejectionHandlerTests
{
    [Fact]
    public async Task Authorization_code_exchange_rejected_with_invalid_client_throws()
    {
        var error = await Assert.ThrowsAsync<McpOAuthClientRejectedException>(() =>
            ExchangeAsync(
                grant: "authorization_code",
                status: HttpStatusCode.BadRequest,
                body: """{"error":"invalid_client"}"""));

        // The message carries the OAuth error code so the manager's message-based checks match.
        Assert.Contains("invalid_client", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_grant_rejected_with_invalid_client_passes_through()
    {
        // A refresh failure must keep the SDK's graceful null path, so the handler stays out
        // of the way even when the error code matches.
        var response = await ExchangeAsync(
            grant: "refresh_token",
            status: HttpStatusCode.BadRequest,
            body: """{"error":"invalid_client"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "invalid_client",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorization_code_exchange_with_other_error_passes_through()
    {
        var response = await ExchangeAsync(
            grant: "authorization_code",
            status: HttpStatusCode.BadRequest,
            body: """{"error":"invalid_grant"}""");

        // The body must survive the handler's inspection so the SDK can still read it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "invalid_grant",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_authorization_code_exchange_passes_through()
    {
        var response = await ExchangeAsync(
            grant: "authorization_code",
            status: HttpStatusCode.OK,
            body: """{"access_token":"token"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "access_token",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_form_request_passes_through_even_on_bad_request()
    {
        // MCP JSON-RPC traffic never carries a grant_type; a 400 with the literal text must
        // not be mistaken for a token rejection.
        using var handler = new OAuthClientRejectionHandler { InnerHandler = new StubHandler(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""") };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0"}""", System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(string grant, HttpStatusCode status, string body)
    {
        var handler = new OAuthClientRejectionHandler { InnerHandler = new StubHandler(status, body) };
        var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = grant,
                ["client_id"] = "client-1",
            }),
        };

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
