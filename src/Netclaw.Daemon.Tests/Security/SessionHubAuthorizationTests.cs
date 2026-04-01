using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// Integration tests verifying that <see cref="SessionHub"/> requires authorization
/// via the authentication/authorization middleware pipeline.
///
/// Uses a minimal <see cref="WebApplication"/> with the loopback scheme wired in the
/// same way as the production daemon, exercising the negotiate endpoint because
/// SignalR authorization is evaluated at the HTTP layer before any hub code runs.
/// </summary>
public sealed class SessionHubAuthorizationTests
{
    /// <summary>
    /// A minimal hub with no constructor dependencies, decorated with [Authorize].
    /// Used here so the integration test does not require the full daemon service graph.
    /// </summary>
    [Authorize]
    private sealed class MinimalHub : Hub { }

    /// <summary>
    /// Creates a minimal test app with loopback auth + authorization + SignalR.
    ///
    /// When <paramref name="spoofedRemoteIp"/> is set, injects a middleware that
    /// sets <see cref="IHttpConnectionFeature.RemoteIpAddress"/> before auth runs,
    /// simulating a connection from that IP. When null, the TestServer default of
    /// no remote IP is used, which the loopback handler treats as non-loopback.
    /// </summary>
    private static async Task<WebApplication> CreateAppAsync(IPAddress? spoofedRemoteIp = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services
            .AddAuthentication(LoopbackAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                LoopbackAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR();

        var app = builder.Build();

        // Inject remote IP before the auth middleware so the loopback handler sees it.
        // DefaultConnectionInfo.RemoteIpAddress lazily creates IHttpConnectionFeature
        // when set, so this works even though TestServer doesn't pre-populate the feature.
        if (spoofedRemoteIp is not null)
        {
            var ip = spoofedRemoteIp;
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = ip;
                await next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<MinimalHub>("/hub/session");
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Non_loopback_connection_receives_401()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        // TestServer has no real TCP stack — RemoteIpAddress is null by default.
        // LoopbackAuthenticationHandler returns NoResult for null → 401.
        var response = await client.PostAsync(
            "/hub/session/negotiate?negotiateVersion=1",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Loopback_connection_passes_authorization()
    {
        await using var app = await CreateAppAsync(spoofedRemoteIp: IPAddress.Loopback);
        var client = app.GetTestClient();

        // RemoteIpAddress set to loopback via middleware → LoopbackAuthenticationHandler
        // issues Operator/LocalProcess ticket → [Authorize] check passes.
        var response = await client.PostAsync(
            "/hub/session/negotiate?negotiateVersion=1",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
