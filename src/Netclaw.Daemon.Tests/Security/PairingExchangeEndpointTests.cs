using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
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
    private readonly string _tempDir;
    private readonly FakeTimeProvider _time;
    private readonly DeviceRegistry _registry;
    private readonly PairingCodeService _pairingCodeService;
    private readonly PairingExchangeGuard _exchangeGuard;

    public PairingExchangeEndpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-exchange-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        _registry = new DeviceRegistry(new NetclawPaths(_tempDir), _time, NullLogger<DeviceRegistry>.Instance);
        _pairingCodeService = new PairingCodeService(_time);
        _exchangeGuard = new PairingExchangeGuard(_time);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(_pairingCodeService);
        builder.Services.AddSingleton(_exchangeGuard);
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddNetclawAuthSchemes();
        builder.Services.AddAuthorization();

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        // Mirror the production exchange endpoint (from Program.cs) without rate limiting,
        // since TestServer doesn't set RemoteIpAddress and rate limiting is orthogonal.
        app.MapPost("/api/pair/exchange", async (
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
        }).AllowAnonymous();

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
}
