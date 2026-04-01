using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// Integration tests verifying that <see cref="SessionHub"/> requires authorization
/// via the authentication/authorization middleware pipeline.
///
/// Uses a minimal <see cref="WebApplication"/> with the selector scheme wired in the
/// same way as the production daemon, exercising the negotiate endpoint because
/// SignalR authorization is evaluated at the HTTP layer before any hub code runs.
/// </summary>
public sealed class SessionHubAuthorizationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeTimeProvider _time;
    private readonly DeviceRegistry _deviceRegistry;

    public SessionHubAuthorizationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-hub-auth-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        var paths = new NetclawPaths(_tempDir);
        _deviceRegistry = new DeviceRegistry(paths, _time, NullLogger<DeviceRegistry>.Instance);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>
    /// A minimal hub with no constructor dependencies, decorated with [Authorize].
    /// Used here so the integration test does not require the full daemon service graph.
    /// </summary>
    [Authorize]
    private sealed class MinimalHub : Hub { }

    /// <summary>
    /// Creates a minimal test app with the multi-scheme selector (AuthSelector →
    /// DeviceBearer when Bearer header present, otherwise Loopback), matching production.
    ///
    /// When <paramref name="spoofedRemoteIp"/> is set, injects a middleware that
    /// sets <see cref="IHttpConnectionFeature.RemoteIpAddress"/> before auth runs,
    /// simulating a connection from that IP. When null, the TestServer default of
    /// no remote IP is used, which the loopback handler treats as non-loopback.
    /// </summary>
    private async Task<WebApplication> CreateAppAsync(IPAddress? spoofedRemoteIp = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_deviceRegistry);
        builder.Services.AddNetclawAuthSchemes();
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR();

        var app = builder.Build();

        // Inject remote IP before the auth middleware so the loopback handler sees it.
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

    private static (string RawToken, PairedDevice Device) MakeDevice(string name, DateTimeOffset createdAt)
        => DeviceTestHelpers.MakeDevice(name, createdAt);

    [Fact]
    public async Task Non_loopback_connection_without_bearer_receives_401()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        // TestServer has no real TCP stack — RemoteIpAddress is null by default.
        // Selector routes to Loopback; Loopback returns NoResult for null → 401.
        var response = await client.PostAsync(
            "/hub/session/negotiate?negotiateVersion=1",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Loopback_connection_without_bearer_passes_authorization()
    {
        await using var app = await CreateAppAsync(spoofedRemoteIp: IPAddress.Loopback);
        var client = app.GetTestClient();

        // Selector routes to Loopback; Loopback issues Operator/LocalProcess ticket.
        var response = await client.PostAsync(
            "/hub/session/negotiate?negotiateVersion=1",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Remote_connection_with_valid_bearer_token_passes_authorization()
    {
        var ct = TestContext.Current.CancellationToken;
        var (rawToken, device) = MakeDevice("aaron-laptop", _time.GetUtcNow());
        await _deviceRegistry.AddAsync(device, ct);

        await using var app = await CreateAppAsync();  // no spoofed IP — remote connection
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawToken);

        // Selector routes to DeviceBearer; valid token → Operator/Verified ticket → passes [Authorize].
        var response = await client.PostAsync(
            "/hub/session/negotiate?negotiateVersion=1",
            content: null,
            ct);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Remote_connection_with_invalid_bearer_token_receives_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, device) = MakeDevice("aaron-laptop", _time.GetUtcNow());
        await _deviceRegistry.AddAsync(device, ct);

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        // Wrong token — DeviceBearer returns Fail → 401.
        var wrongToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", wrongToken);

        var response = await client.PostAsync(
            "/hub/session/negotiate?negotiateVersion=1",
            content: null,
            ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
