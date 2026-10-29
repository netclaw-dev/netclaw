// -----------------------------------------------------------------------
// <copyright file="McpOAuthClientRegistrarTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpOAuthClientRegistrarTests
{
    /// <summary>
    /// GitHub's issuer is https://github.com/login/oauth, and it publishes its metadata
    /// only at the RFC 8414 §3 location, where the well-known segment precedes the
    /// issuer path. The appended form returns 404.
    /// </summary>
    [Fact]
    public async Task Issuer_with_path_discovers_metadata_at_rfc8414_location()
    {
        var handler = new StubHandler(new Dictionary<string, string>
        {
            ["https://mcp.example/.well-known/oauth-protected-resource/mcp"] =
                """{"authorization_servers":["https://auth.example/login/oauth"]}""",
            ["https://auth.example/.well-known/oauth-authorization-server/login/oauth"] =
                """{"issuer":"https://auth.example/login/oauth","registration_endpoint":"https://auth.example/register"}""",
            ["https://auth.example/register"] = """{"client_id":"registered-client"}""",
        });
        var registrar = McpOAuthTestDoubles.RegistrarFor(new HttpClient(handler));

        var identity = await registrar.TryRegisterAsync(
            new McpServerName("github"),
            "https://mcp.example/mcp/",
            new Uri("http://127.0.0.1:5199/callback"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(identity);
        Assert.Equal("registered-client", identity.ClientId);
    }

    private sealed class StubHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responses.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
