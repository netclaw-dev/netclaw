// -----------------------------------------------------------------------
// <copyright file="SessionHubAuthorizationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
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
    private const string ObservedRemoteIpHeader = "X-Test-Observed-Remote-IP";

    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _time;
    private readonly DeviceRegistry _deviceRegistry;

    public SessionHubAuthorizationTests()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        var paths = new NetclawPaths(_dir.Path);
        _deviceRegistry = new DeviceRegistry(paths, _time, NullLogger<DeviceRegistry>.Instance);
    }

    public void Dispose() => _dir.Dispose();

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
    private async Task<WebApplication> CreateAppAsync(
        IPAddress? spoofedRemoteIp = null,
        DaemonConfig? daemonConfig = null)
    {
        daemonConfig ??= new DaemonConfig();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_deviceRegistry);
        builder.Services.AddNetclawAuthSchemes(daemonConfig);
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

        if (daemonConfig.ExposureMode == ExposureMode.ReverseProxy)
        {
            var forwardedHeadersOptions = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                ForwardLimit = 1
            };

            foreach (var trustedProxy in DaemonExposureValidator.ParseTrustedProxies(daemonConfig.TrustedProxies))
            {
                if (trustedProxy.PrefixLength is null)
                {
                    forwardedHeadersOptions.KnownProxies.Add(trustedProxy.Address);
                }
                else
                {
                    forwardedHeadersOptions.KnownIPNetworks.Add(
                        new System.Net.IPNetwork(trustedProxy.Address, trustedProxy.PrefixLength.Value));
                }
            }

            app.UseForwardedHeaders(forwardedHeadersOptions);
        }

        app.Use(async (ctx, next) =>
        {
            var observedRemoteIp = ctx.Connection.RemoteIpAddress?.ToString();
            ctx.Response.OnStarting(() =>
            {
                if (!string.IsNullOrWhiteSpace(observedRemoteIp))
                    ctx.Response.Headers[ObservedRemoteIpHeader] = observedRemoteIp;

                return Task.CompletedTask;
            });

            await next(ctx);
        });

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

    [Fact]
    public async Task Reverse_proxy_trusted_forwarded_client_ip_is_seen_before_auth_evaluation()
    {
        var daemonConfig = new DaemonConfig
        {
            ExposureMode = ExposureMode.ReverseProxy,
            Host = "10.0.0.10",
            TrustedProxies = ["10.0.0.5"]
        };

        await using var app = await CreateAppAsync(
            spoofedRemoteIp: IPAddress.Parse("10.0.0.5"),
            daemonConfig: daemonConfig);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/hub/session/negotiate?negotiateVersion=1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.25");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("198.51.100.25", GetObservedRemoteIp(response));
    }

    [Fact]
    public async Task Reverse_proxy_trusted_forwarded_client_ip_allows_valid_bearer_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        var (rawToken, device) = MakeDevice("proxy-laptop", _time.GetUtcNow());
        await _deviceRegistry.AddAsync(device, ct);

        var daemonConfig = new DaemonConfig
        {
            ExposureMode = ExposureMode.ReverseProxy,
            Host = "10.0.0.10",
            TrustedProxies = ["10.0.0.5"]
        };

        await using var app = await CreateAppAsync(
            spoofedRemoteIp: IPAddress.Parse("10.0.0.5"),
            daemonConfig: daemonConfig);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/hub/session/negotiate?negotiateVersion=1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.26");

        var response = await client.SendAsync(request, ct);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("198.51.100.26", GetObservedRemoteIp(response));
    }

    [Fact]
    public async Task Reverse_proxy_ignores_forwarded_headers_from_untrusted_proxy_peer()
    {
        var daemonConfig = new DaemonConfig
        {
            ExposureMode = ExposureMode.ReverseProxy,
            Host = "10.0.0.10",
            TrustedProxies = ["10.0.0.5"]
        };

        await using var app = await CreateAppAsync(
            spoofedRemoteIp: IPAddress.Parse("203.0.113.10"),
            daemonConfig: daemonConfig);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/hub/session/negotiate?negotiateVersion=1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("203.0.113.10", GetObservedRemoteIp(response));
    }

    private static string GetObservedRemoteIp(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(ObservedRemoteIpHeader, out var values));
        return Assert.Single(values);
    }

    [Fact]
    public async Task Reverse_proxy_remote_bootstrap_bearer_token_cannot_invoke_daemon_pair()
    {
        var hub = CreateSessionHub(
            remoteIp: IPAddress.Parse("198.51.100.26"),
            transport: nameof(TransportAuthenticity.Verified),
            isBootstrapDevice: true);

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.GeneratePairingCode());

        Assert.Equal(
            "GeneratePairingCode requires a daemon-host local operator connection or direct authenticated local control-plane access.",
            ex.Message);
    }

    [Fact]
    public async Task Loopback_verified_bearer_token_can_invoke_daemon_pair()
    {
        var hub = CreateSessionHub(
            remoteIp: IPAddress.Loopback,
            transport: nameof(TransportAuthenticity.Verified),
            isBootstrapDevice: false);

        var result = await hub.GeneratePairingCode();

        Assert.Matches("^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}$", result.FormattedCode);
        Assert.Equal(_time.GetUtcNow().AddMinutes(5), result.ExpiresAt);
    }

    [Fact]
    public async Task Same_host_reverse_proxy_verified_bearer_token_can_invoke_daemon_pair()
    {
        var hub = CreateSessionHub(
            remoteIp: IPAddress.Parse("10.0.0.10"),
            localIp: IPAddress.Parse("10.0.0.10"),
            transport: nameof(TransportAuthenticity.Verified),
            isBootstrapDevice: false);

        var result = await hub.GeneratePairingCode();

        Assert.Matches("^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}$", result.FormattedCode);
        Assert.Equal(_time.GetUtcNow().AddMinutes(5), result.ExpiresAt);
    }

    [Fact]
    public void IsDirectLocalControlPlaneConnection_ReturnsTrue_WhenRemoteMatchesLocal()
    {
        // Non-loopback equality between remote and local IPs indicates an on-host
        // connection; exposure mode does not affect this branch (governed by
        // UseForwardedHeaders wiring elsewhere).
        Assert.True(SessionHub.IsDirectLocalControlPlaneConnection(
            IPAddress.Parse("10.0.0.10"),
            IPAddress.Parse("10.0.0.10"),
            ExposureMode.ReverseProxy));
    }

    [Fact]
    public void IsDirectLocalControlPlaneConnection_ReturnsFalse_WhenRemoteDiffersFromLocal()
    {
        Assert.False(SessionHub.IsDirectLocalControlPlaneConnection(
            IPAddress.Parse("198.51.100.26"),
            IPAddress.Parse("10.0.0.10"),
            ExposureMode.ReverseProxy));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void IsDirectLocalControlPlaneConnection_Loopback_LocalMode_ReturnsTrue(string loopbackIp)
    {
        // ExposureMode.Local is the only mode that treats loopback as proof of a
        // same-host caller; tunnel / reverse-proxy modes forward remote traffic
        // over loopback.
        Assert.True(SessionHub.IsDirectLocalControlPlaneConnection(
            IPAddress.Parse(loopbackIp),
            IPAddress.None,
            ExposureMode.Local));
    }

    [Theory]
    [InlineData(ExposureMode.ReverseProxy, "127.0.0.1")]
    [InlineData(ExposureMode.ReverseProxy, "::1")]
    [InlineData(ExposureMode.TailscaleServe, "127.0.0.1")]
    [InlineData(ExposureMode.TailscaleServe, "::1")]
    [InlineData(ExposureMode.TailscaleFunnel, "127.0.0.1")]
    [InlineData(ExposureMode.TailscaleFunnel, "::1")]
    [InlineData(ExposureMode.CloudflareTunnel, "127.0.0.1")]
    [InlineData(ExposureMode.CloudflareTunnel, "::1")]
    public void IsDirectLocalControlPlaneConnection_Loopback_NonLocalMode_ReturnsFalse(
        ExposureMode mode, string loopbackIp)
    {
        // Under any exposure mode that accepts remote traffic, a loopback remote IP
        // is the local tunnel agent or reverse-proxy peer, NOT a direct on-host
        // operator. Verifies the audit #24 follow-up: a paired (Verified) remote
        // device whose connection arrives over loopback must not satisfy the
        // "direct local control plane" predicate that gates GeneratePairingCode.
        Assert.False(SessionHub.IsDirectLocalControlPlaneConnection(
            IPAddress.Parse(loopbackIp),
            IPAddress.None,
            mode));
    }

    [Theory]
    [InlineData(ExposureMode.TailscaleServe)]
    [InlineData(ExposureMode.TailscaleFunnel)]
    [InlineData(ExposureMode.CloudflareTunnel)]
    public async Task Tunnel_mode_verified_bearer_token_over_loopback_cannot_invoke_daemon_pair(
        ExposureMode mode)
    {
        // Post-#24 the loopback handler no longer auto-issues LocalProcess for
        // tunnel modes, but a paired (Verified) device's connection still arrives
        // at the daemon over loopback (via tailscaled/cloudflared). The pairing
        // gate must not treat that loopback peer as "direct local control plane"
        // or paired remote devices could mint additional pairing codes through
        // the tunnel — defeating the documented "remote paired devices cannot
        // mint new codes" rule.
        var hub = CreateSessionHub(
            remoteIp: IPAddress.Loopback,
            transport: nameof(TransportAuthenticity.Verified),
            isBootstrapDevice: false,
            exposureMode: mode);

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.GeneratePairingCode());

        Assert.Equal(
            "GeneratePairingCode requires a daemon-host local operator connection or direct authenticated local control-plane access.",
            ex.Message);
    }

    private SessionHub CreateSessionHub(
        IPAddress remoteIp,
        string transport,
        bool isBootstrapDevice,
        IPAddress? localIp = null,
        ExposureMode exposureMode = ExposureMode.Local)
    {
        var pairingCodeService = new PairingCodeService(_time);
        var claims = new List<Claim>
        {
            new(NetclawClaimTypes.TransportAuthenticity, transport),
            new(NetclawClaimTypes.BootstrapDevice, isBootstrapDevice.ToString())
        };

        var hub = new SessionHub(
            registry: null!,
            pairingCodeService,
            new DaemonConfig { ExposureMode = exposureMode },
            NullLogger<SessionHub>.Instance)
        {
            Context = new TestHubCallerContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), remoteIp, localIp)
        };

        return hub;
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly HttpContext _httpContext;

        public TestHubCallerContext(ClaimsPrincipal user, IPAddress remoteIp, IPAddress? localIp)
        {
            User = user;
            ConnectionId = Guid.NewGuid().ToString("N");
            UserIdentifier = user.Identity?.Name;
            Items = new Dictionary<object, object?>();
            Features = new FeatureCollection();
            _httpContext = new DefaultHttpContext
            {
                Connection =
                {
                    RemoteIpAddress = remoteIp,
                    LocalIpAddress = localIp ?? IPAddress.Loopback
                }
            };
            Features.Set<IHttpContextFeature>(new TestHttpContextFeature(_httpContext));
        }

        public override string ConnectionId { get; }

        public override string? UserIdentifier { get; }

        public override ClaimsPrincipal User { get; }

        public override IDictionary<object, object?> Items { get; }

        public override IFeatureCollection Features { get; }

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() { }
    }

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public TestHttpContextFeature(HttpContext httpContext)
        {
            HttpContext = httpContext;
        }

        public HttpContext? HttpContext { get; set; }
    }
}
