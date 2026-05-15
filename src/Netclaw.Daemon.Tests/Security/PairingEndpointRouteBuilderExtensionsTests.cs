// -----------------------------------------------------------------------
// <copyright file="PairingEndpointRouteBuilderExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// Real integration tests for the pairing endpoints registered by
/// <see cref="PairingEndpointRouteBuilderExtensions.MapPairingEndpoints"/>.
///
/// The test host calls the actual extension method — no handler reimplementation.
/// </summary>
public sealed class PairingEndpointRouteBuilderExtensionsTests : IAsyncDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _time;
    private readonly DeviceRegistry _registry;
    private readonly PairingCodeService _pairingCodeService;
    private readonly PairingExchangeGuard _exchangeGuard;

    public PairingEndpointRouteBuilderExtensionsTests()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        _registry = new DeviceRegistry(new NetclawPaths(_dir.Path), _time, NullLogger<DeviceRegistry>.Instance);
        _pairingCodeService = new PairingCodeService(_time);
        _exchangeGuard = new PairingExchangeGuard(_time);
    }

    public ValueTask DisposeAsync()
    {
        _dir.Dispose();
        return ValueTask.CompletedTask;
    }

    // ─── App factory ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a test host wired with the real <see cref="PairingEndpointRouteBuilderExtensions.MapPairingEndpoints"/>.
    ///
    /// The "pairing-exchange" rate-limiter is registered with a very high permit limit so that
    /// the ASP.NET framework limiter never fires during tests — we test the guard lockout
    /// (<see cref="PairingExchangeGuard"/>) specifically, not the framework limiter.
    /// </summary>
    private async Task<WebApplication> CreateAppAsync(
        bool spoofLoopback = false,
        IPAddress? remoteIp = null,
        string[]? trustedProxies = null,
        bool useRealRateLimiter = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(_pairingCodeService);
        builder.Services.AddSingleton(_exchangeGuard);
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();

        // Most tests use a very high permit limit so the ASP.NET rate limiter never fires —
        // the guard lockout under test is PairingExchangeGuard (Layer 1). Tests that
        // specifically exercise the framework limiter pass useRealRateLimiter: true to get
        // the production permit limit (5/min/IP).
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("pairing-exchange", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = useRealRateLimiter ? 5 : 10_000,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    }));
            options.RejectionStatusCode = 429;
        });

        var app = builder.Build();

        if (spoofLoopback || remoteIp is not null)
        {
            var ip = remoteIp ?? IPAddress.Loopback;
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = ip;
                await next(ctx);
            });
        }

        // Mirror the daemon's reverse-proxy wiring: when trusted proxies are configured,
        // UseForwardedHeaders rewrites RemoteIpAddress to the X-Forwarded-For client IP
        // (only when the direct peer is a known proxy). Must run after the direct-peer IP
        // is set above and before the rate limiter / endpoint read RemoteIpAddress.
        if (trustedProxies is not null)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor,
                ForwardLimit = 1,
            };
            foreach (var proxy in trustedProxies)
                forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
            app.UseForwardedHeaders(forwarded);
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapPairingEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    // ─── POST /api/pair/exchange ───────────────────────────────────────────────

    /// <summary>Test case 1: no pending code → 404 (endpoint hidden).</summary>
    [Fact]
    public async Task Exchange_returns_404_when_no_code_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code = "ABCD-EFGH", deviceName = "laptop" }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Test case 2: valid pending code, anonymous caller → 200 with token;
    /// device is registered in DeviceRegistry.
    /// Also proves .AllowAnonymous() is wired — no auth header supplied.
    /// </summary>
    [Fact]
    public async Task Exchange_returns_200_with_token_and_registers_device_for_valid_code()
    {
        var ct = TestContext.Current.CancellationToken;
        // Produce a known pending code by calling GenerateCode() directly on the service.
        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();
        // No Authorization header — proves AllowAnonymous is wired.

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "my-laptop" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(body.TryGetProperty("token", out var tokenProp));
        Assert.False(string.IsNullOrWhiteSpace(tokenProp.GetString()));

        var devices = await _registry.ListAsync(ct);
        Assert.Single(devices);
        Assert.Equal("my-laptop", devices[0].Name);
    }

    /// <summary>
    /// Test case 3: invalid code → 401; failure recorded on PairingExchangeGuard.
    /// </summary>
    [Fact]
    public async Task Exchange_returns_401_for_invalid_code_and_records_guard_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        _pairingCodeService.GenerateCode(); // ensure a code is pending so the gate opens
        var remoteIp = IPAddress.Parse("10.0.0.1");

        await using var app = await CreateAppAsync(remoteIp: remoteIp);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code = "ZZZZ-ZZZZ", deviceName = "laptop" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Guard should have recorded exactly one failure for this IP.
        // Drive to threshold – 1 more attempts, then the very next should be blocked.
        for (var i = 1; i < PairingExchangeGuard.FailureThreshold; i++)
        {
            _pairingCodeService.GenerateCode();
            var r = await client.PostAsJsonAsync("/api/pair/exchange",
                new { code = "ZZZZ-ZZZZ", deviceName = "laptop" }, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        // One more pending code, then the IP should now be blocked.
        _pairingCodeService.GenerateCode();
        var blocked = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code = "ZZZZ-ZZZZ", deviceName = "laptop" }, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    /// <summary>
    /// Test case 4: guard-blocked IP → 429 with Retry-After header.
    /// Pre-seeds the guard to the blocked state by driving FailureThreshold failures,
    /// then verifies the next request (with a valid pending code) is still blocked.
    /// </summary>
    [Fact]
    public async Task Exchange_returns_429_with_RetryAfter_when_guard_has_blocked_ip()
    {
        var ct = TestContext.Current.CancellationToken;
        var remoteIp = IPAddress.Parse("10.0.0.2");

        // Pre-block the IP by recording failures directly on the guard.
        for (var i = 0; i < PairingExchangeGuard.FailureThreshold; i++)
            _exchangeGuard.RecordFailure(remoteIp);

        // Even with a valid pending code, the guard blocks before any code check.
        _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync(remoteIp: remoteIp);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code = "ABCD-EFGH", deviceName = "laptop" }, ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.NotEmpty(values);
    }

    /// <summary>Test case 5: missing code or deviceName → 400.</summary>
    [Fact]
    public async Task Exchange_returns_400_when_code_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code = "", deviceName = "laptop" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_returns_400_when_device_name_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Test case 6: duplicate device name → 409.
    /// Seeds the registry with an existing device, then submits a valid code with the same name.
    /// </summary>
    [Fact]
    public async Task Exchange_returns_409_for_duplicate_device_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, existingDevice) = DeviceTestHelpers.MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(existingDevice, ct);

        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "Laptop" }, ct); // case-insensitive duplicate

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>Coverage from old tests: code already consumed → 404 on second attempt.</summary>
    [Fact]
    public async Task Exchange_returns_404_when_code_already_consumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var first = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "laptop" }, ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Code is consumed; second attempt sees no pending code → 404.
        var second = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "phone" }, ct);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    /// <summary>Coverage from old tests: expired code → 404.</summary>
    [Fact]
    public async Task Exchange_returns_404_when_code_is_expired()
    {
        var ct = TestContext.Current.CancellationToken;
        _pairingCodeService.GenerateCode();
        // Advance past the 5-minute TTL.
        _time.Advance(TimeSpan.FromMinutes(6));

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code = "ABCD-EFGH", deviceName = "laptop" }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Coverage from old tests: the returned bearer token authenticates a subsequent
    /// request to an authorized endpoint.
    /// </summary>
    [Fact]
    public async Task Exchange_returned_token_authenticates_against_authorized_endpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var exchangeResponse = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "phone" }, ct);
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);

        var body = await exchangeResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var token = body.GetProperty("token").GetString()!;

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var devicesResponse = await client.GetAsync("/api/pair/devices", ct);
        Assert.Equal(HttpStatusCode.OK, devicesResponse.StatusCode);
    }

    // ─── GET /api/pair/devices ─────────────────────────────────────────────────

    /// <summary>Test case 7a: unauthenticated GET → 401.</summary>
    [Fact]
    public async Task List_devices_returns_401_for_unauthenticated_request()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/pair/devices", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Test case 7b: authenticated (loopback) GET → 200 with sanitized list
    /// (no TokenHash/Salt fields in the response).
    /// </summary>
    [Fact]
    public async Task List_devices_returns_sanitized_list_for_loopback_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, laptop) = DeviceTestHelpers.MakeDevice("laptop", _time.GetUtcNow());
        var (_, phone) = DeviceTestHelpers.MakeDevice("phone", _time.GetUtcNow());
        await _registry.AddAsync(laptop, ct);
        await _registry.AddAsync(phone, ct);

        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/pair/devices", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var devices = await response.Content.ReadFromJsonAsync<List<PairedDeviceInfoDto>>(ct);
        Assert.NotNull(devices);
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Name == "laptop");
        Assert.Contains(devices, d => d.Name == "phone");

        // Verify no token hash / salt fields leak through by checking the raw JSON.
        var raw = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("salt", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ─── DELETE /api/pair/devices/{name} ──────────────────────────────────────

    /// <summary>Test case 8a: unauthenticated DELETE → 401.</summary>
    [Fact]
    public async Task Revoke_device_returns_401_for_unauthenticated_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, device) = DeviceTestHelpers.MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(device, ct);

        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync("/api/pair/devices/laptop", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Test case 8b: authenticated DELETE of existing device → 204.</summary>
    [Fact]
    public async Task Revoke_device_returns_204_and_removes_it_for_loopback_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, device) = DeviceTestHelpers.MakeDevice("laptop", _time.GetUtcNow());
        await _registry.AddAsync(device, ct);

        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync("/api/pair/devices/laptop", ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var remaining = await _registry.ListAsync(ct);
        Assert.Empty(remaining);
    }

    /// <summary>Test case 8c: authenticated DELETE of missing device → 404.</summary>
    [Fact]
    public async Task Revoke_device_returns_404_when_device_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync("/api/pair/devices/nonexistent", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Reverse-proxy: per-IP defenses key on the forwarded client IP ─────────

    /// <summary>
    /// Behind a trusted reverse proxy, the per-IP failure lockout
    /// (<see cref="PairingExchangeGuard"/>) must key on the real client IP from
    /// <c>X-Forwarded-For</c> — not the proxy's address. Otherwise one abusive
    /// client would lock out every client sharing the proxy, and a client could
    /// not be individually locked out at all.
    /// </summary>
    [Fact]
    public async Task ReverseProxy_guard_locks_out_by_forwarded_client_ip()
    {
        var ct = TestContext.Current.CancellationToken;
        _pairingCodeService.GenerateCode();

        // The direct peer is the trusted proxy; UseForwardedHeaders rewrites the
        // request IP to the X-Forwarded-For client.
        await using var app = await CreateAppAsync(
            remoteIp: IPAddress.Parse("10.0.0.5"),
            trustedProxies: ["10.0.0.5"]);
        var client = app.GetTestClient();

        for (var i = 0; i < PairingExchangeGuard.FailureThreshold; i++)
        {
            var failed = await PostExchangeAsync(client, "ZZZZ-ZZZZ", "laptop", "198.51.100.20", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // The next attempt from that forwarded client IP is locked out.
        var blocked = await PostExchangeAsync(client, "ZZZZ-ZZZZ", "laptop", "198.51.100.20", ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // A different forwarded client behind the same proxy is unaffected — the
        // lockout is per real client IP, not per proxy.
        var other = await PostExchangeAsync(client, "ZZZZ-ZZZZ", "laptop", "198.51.100.21", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    /// <summary>
    /// Behind a trusted reverse proxy, the ASP.NET rate limiter must partition by
    /// the forwarded client IP, so the brute-force window is per real client and
    /// cannot be evaded by, or shared across, clients behind the proxy.
    /// </summary>
    [Fact]
    public async Task ReverseProxy_rate_limiter_partitions_by_forwarded_client_ip()
    {
        var ct = TestContext.Current.CancellationToken;
        _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync(
            remoteIp: IPAddress.Parse("10.0.0.5"),
            trustedProxies: ["10.0.0.5"],
            useRealRateLimiter: true); // production 5/min/IP limit
        var client = app.GetTestClient();

        // Exhaust the 5-request window for one forwarded client IP (well under the
        // guard's failure threshold, so the limiter — not the guard — is what fires).
        for (var i = 0; i < 5; i++)
        {
            var r = await PostExchangeAsync(client, "ZZZZ-ZZZZ", "laptop", "198.51.100.30", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        var limited = await PostExchangeAsync(client, "ZZZZ-ZZZZ", "laptop", "198.51.100.30", ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // A different forwarded client has its own window.
        var other = await PostExchangeAsync(client, "ZZZZ-ZZZZ", "laptop", "198.51.100.31", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    private static Task<HttpResponseMessage> PostExchangeAsync(
        HttpClient client,
        string code,
        string deviceName,
        string forwardedFor,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/pair/exchange")
        {
            Content = JsonContent.Create(new { code, deviceName }),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request, ct);
    }
}
