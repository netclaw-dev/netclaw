// -----------------------------------------------------------------------
// <copyright file="PairingExchangeEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
/// Integration tests for the <c>POST /api/pair/exchange</c> endpoint.
///
/// Validates HTTP-level behavior: 200 on valid exchange, 400 on missing fields,
/// 401 on invalid/expired/reused codes, and that the returned token authenticates
/// subsequent requests.
/// </summary>
public sealed class PairingExchangeEndpointTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _time;
    private readonly DeviceRegistry _registry;
    private readonly PairingCodeService _pairingCodeService;
    private readonly PairingExchangeGuard _exchangeGuard;

    public PairingExchangeEndpointTests()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        _registry = new DeviceRegistry(new NetclawPaths(_dir.Path), _time, NullLogger<DeviceRegistry>.Instance);
        _pairingCodeService = new PairingCodeService(_time);
        _exchangeGuard = new PairingExchangeGuard(_time);
    }

    public void Dispose() => _dir.Dispose();

    private async Task<WebApplication> CreateAppAsync(
        DaemonConfig? daemonConfig = null,
        IPAddress? directPeerIp = null,
        bool enableRateLimiting = false)
    {
        daemonConfig ??= new DaemonConfig();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(_pairingCodeService);
        builder.Services.AddSingleton(_exchangeGuard);
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddNetclawAuthSchemes(daemonConfig);
        builder.Services.AddAuthorization();

        if (enableRateLimiting)
        {
            builder.Services.AddRateLimiter(options =>
            {
                options.AddPolicy("pairing-exchange", context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 5,
                            Window = TimeSpan.FromMinutes(1),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0,
                        }));
                options.RejectionStatusCode = 429;
            });
        }

        var app = builder.Build();

        if (directPeerIp is not null)
        {
            var ip = directPeerIp;
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

        app.UseAuthentication();
        app.UseAuthorization();

        if (enableRateLimiting)
            app.UseRateLimiter();

        var exchangeEndpoint = app.MapPost("/api/pair/exchange", async (
            HttpContext httpContext,
            PairingCodeExchangeRequest request,
            PairingCodeService pairingCodeService,
            PairingExchangeGuard exchangeGuard,
            DeviceRegistry deviceRegistry,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var remoteIp = httpContext.Connection.RemoteIpAddress;

            if (exchangeGuard.IsBlocked(remoteIp))
            {
                var retryAfter = exchangeGuard.GetRetryAfterSeconds(remoteIp);
                httpContext.Response.Headers.RetryAfter = retryAfter?.ToString() ?? "900";
                return Results.Json(
                    new { error = "Too many failed attempts. Try again later." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (pairingCodeService.GetPendingExpiry() is null)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.DeviceName))
                return Results.BadRequest(new { error = "code and deviceName are required." });

            if (!pairingCodeService.TryConsume(request.Code))
            {
                exchangeGuard.RecordFailure(remoteIp);
                return Results.Json(
                    new { error = "Invalid, expired, or already-used pairing code." },
                    statusCode: 401);
            }

            var tokenBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var rawToken = System.Buffers.Text.Base64Url.EncodeToString(tokenBytes);

            var saltBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
            var tokenHash = DeviceRegistry.ComputeTokenHash(rawToken, saltHex);

            var now = timeProvider.GetUtcNow();
            var device = new PairedDevice
            {
                Name = request.DeviceName.Trim(),
                TokenHash = tokenHash,
                Salt = saltHex,
                CreatedAt = now,
                LastUsedAt = now,
            };

            try
            {
                await deviceRegistry.AddAsync(device, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            return Results.Ok(new { token = rawToken });
        });

        if (enableRateLimiting)
            exchangeEndpoint.RequireRateLimiting("pairing-exchange");

        exchangeEndpoint.AllowAnonymous();

        // Authenticated endpoint to verify returned tokens work
        app.MapGet("/api/pair/devices", async (DeviceRegistry deviceRegistry, CancellationToken ct) =>
        {
            var devices = await deviceRegistry.ListAsync(ct);
            var sanitized = devices.Select(d => new PairedDeviceInfoDto(d.Name, d.CreatedAt, d.LastUsedAt));
            return Results.Ok(sanitized);
        }).RequireAuthorization();

        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Exchange_returns_200_with_token_for_valid_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "laptop" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(body.TryGetProperty("token", out var tokenProp));
        Assert.False(string.IsNullOrWhiteSpace(tokenProp.GetString()));
    }

    [Fact]
    public async Task Exchange_registers_device_and_token_authenticates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        // Exchange the code
        var exchangeResponse = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "phone" }, ct);
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);

        var body = await exchangeResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var token = body.GetProperty("token").GetString()!;

        // Use the returned token to hit an authenticated endpoint
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var devicesResponse = await client.GetAsync("/api/pair/devices", ct);
        Assert.Equal(HttpStatusCode.OK, devicesResponse.StatusCode);

        var devices = await devicesResponse.Content.ReadFromJsonAsync<List<PairedDeviceInfoDto>>(ct);
        Assert.NotNull(devices);
        Assert.Single(devices);
        Assert.Equal("phone", devices[0].Name);
    }

    [Fact]
    public async Task Exchange_returns_400_when_code_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        _pairingCodeService.GenerateCode(); // ensure a code is pending so the gate lets us through

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code = "", deviceName = "laptop" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_returns_400_when_device_name_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_returns_401_for_wrong_code()
    {
        var ct = TestContext.Current.CancellationToken;
        _pairingCodeService.GenerateCode(); // generate a real code, but present a wrong one

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code = "ZZZZ-ZZZZ", deviceName = "laptop" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

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

    [Fact]
    public async Task Exchange_returns_404_when_code_already_consumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        // First exchange succeeds
        var first = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "laptop" }, ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second exchange with same code fails — no pending code means 404
        var second = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "phone" }, ct);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Exchange_returns_404_when_code_expired()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = _pairingCodeService.GenerateCode();

        // Advance time past the 5-minute TTL
        _time.Advance(TimeSpan.FromMinutes(6));

        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        // Expired code means GetPendingExpiry() returns null → 404
        var response = await client.PostAsJsonAsync("/api/pair/exchange",
            new { code, deviceName = "laptop" }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

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
            new { code, deviceName = "Laptop" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ReverseProxy_PairingGuard_UsesForwardedClientIp_FromTrustedProxy()
    {
        var ct = TestContext.Current.CancellationToken;
        _pairingCodeService.GenerateCode();

        var daemonConfig = new DaemonConfig
        {
            ExposureMode = ExposureMode.ReverseProxy,
            Host = "10.0.0.10",
            TrustedProxies = ["10.0.0.5"]
        };

        await using var app = await CreateAppAsync(
            daemonConfig: daemonConfig,
            directPeerIp: IPAddress.Parse("10.0.0.5"));
        var client = app.GetTestClient();

        for (var i = 0; i < PairingExchangeGuard.FailureThreshold; i++)
        {
            var failedResponse = await PostExchangeAsync(
                client,
                code: "ZZZZ-ZZZZ",
                deviceName: "laptop",
                forwardedFor: "198.51.100.20",
                ct);

            Assert.Equal(HttpStatusCode.Unauthorized, failedResponse.StatusCode);
        }

        var blockedResponse = await PostExchangeAsync(
            client,
            code: "ZZZZ-ZZZZ",
            deviceName: "laptop",
            forwardedFor: "198.51.100.20",
            ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, blockedResponse.StatusCode);
        Assert.True(blockedResponse.Headers.TryGetValues("Retry-After", out _));

        var otherForwardedClientResponse = await PostExchangeAsync(
            client,
            code: "ZZZZ-ZZZZ",
            deviceName: "laptop",
            forwardedFor: "198.51.100.21",
            ct);

        Assert.Equal(HttpStatusCode.Unauthorized, otherForwardedClientResponse.StatusCode);
    }

    [Fact]
    public async Task ReverseProxy_RateLimiter_UsesForwardedClientIp_FromTrustedProxy()
    {
        var ct = TestContext.Current.CancellationToken;
        _pairingCodeService.GenerateCode();

        var daemonConfig = new DaemonConfig
        {
            ExposureMode = ExposureMode.ReverseProxy,
            Host = "10.0.0.10",
            TrustedProxies = ["10.0.0.5"]
        };

        await using var app = await CreateAppAsync(
            daemonConfig: daemonConfig,
            directPeerIp: IPAddress.Parse("10.0.0.5"),
            enableRateLimiting: true);
        var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
        {
            var response = await PostExchangeAsync(
                client,
                code: "ZZZZ-ZZZZ",
                deviceName: "laptop",
                forwardedFor: "198.51.100.30",
                ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var limitedResponse = await PostExchangeAsync(
            client,
            code: "ZZZZ-ZZZZ",
            deviceName: "laptop",
            forwardedFor: "198.51.100.30",
            ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);

        var otherForwardedClientResponse = await PostExchangeAsync(
            client,
            code: "ZZZZ-ZZZZ",
            deviceName: "laptop",
            forwardedFor: "198.51.100.31",
            ct);

        Assert.Equal(HttpStatusCode.Unauthorized, otherForwardedClientResponse.StatusCode);
    }

    private static Task<HttpResponseMessage> PostExchangeAsync(
        HttpClient client,
        string code,
        string deviceName,
        string? forwardedFor,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/pair/exchange")
        {
            Content = JsonContent.Create(new { code, deviceName })
        };

        if (!string.IsNullOrWhiteSpace(forwardedFor))
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);

        return client.SendAsync(request, ct);
    }
}
