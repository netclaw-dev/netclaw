using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// Unit tests for <see cref="DeviceTokenAuthenticationHandler"/>.
/// Verifies that valid tokens succeed with correct claims, invalid tokens fail,
/// and missing/malformed Authorization headers produce NoResult.
/// </summary>
public sealed class DeviceTokenAuthenticationHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeTimeProvider _time;
    private readonly DeviceRegistry _deviceRegistry;

    public DeviceTokenAuthenticationHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-auth-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        var paths = new NetclawPaths(_tempDir);
        _deviceRegistry = new DeviceRegistry(paths, _time, NullLogger<DeviceRegistry>.Instance);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_deviceRegistry);
        services.AddSingleton<TimeProvider>(_time);
        services
            .AddAuthentication(DeviceTokenAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(
                DeviceTokenAuthenticationHandler.SchemeName, _ => { });
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext BuildContext(string? authorizationHeader, IServiceProvider sp)
    {
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.50");
        if (authorizationHeader is not null)
            ctx.Request.Headers.Authorization = authorizationHeader;
        return ctx;
    }

    private static (string RawToken, PairedDevice Device) MakeDevice(
        string name, DateTimeOffset createdAt)
        => DeviceTestHelpers.MakeDevice(name, createdAt);

    // ── Valid token ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_token_returns_success_with_operator_verified_claims()
    {
        var ct = TestContext.Current.CancellationToken;
        var (rawToken, device) = MakeDevice("aaron-laptop", _time.GetUtcNow());
        await _deviceRegistry.AddAsync(device, ct);

        var sp = BuildServiceProvider();
        var ctx = BuildContext($"Bearer {rawToken}", sp);
        var authService = sp.GetRequiredService<IAuthenticationService>();

        var result = await authService.AuthenticateAsync(ctx, DeviceTokenAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);

        var principal = result.Principal!;
        Assert.Equal(
            nameof(PrincipalClassification.Operator),
            principal.FindFirst(NetclawClaimTypes.PrincipalClassification)?.Value);
        Assert.Equal(
            nameof(TransportAuthenticity.Verified),
            principal.FindFirst(NetclawClaimTypes.TransportAuthenticity)?.Value);
        Assert.Equal(
            "aaron-laptop",
            principal.FindFirst(NetclawClaimTypes.DeviceId)?.Value);
    }

    [Fact]
    public async Task Valid_token_updates_last_used_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var createdAt = _time.GetUtcNow();
        var (rawToken, device) = MakeDevice("aaron-laptop", createdAt);
        await _deviceRegistry.AddAsync(device, ct);

        _time.Advance(TimeSpan.FromHours(1));
        var expectedLastUsed = _time.GetUtcNow();

        var sp = BuildServiceProvider();
        var ctx = BuildContext($"Bearer {rawToken}", sp);
        await sp.GetRequiredService<IAuthenticationService>()
            .AuthenticateAsync(ctx, DeviceTokenAuthenticationHandler.SchemeName);

        var devices = await _deviceRegistry.ListAsync(ct);
        Assert.Equal(expectedLastUsed, devices[0].LastUsedAt);
    }

    // ── Invalid token ────────────────────────────────────────────────────────

    [Fact]
    public async Task Invalid_token_returns_fail()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, device) = MakeDevice("aaron-laptop", _time.GetUtcNow());
        await _deviceRegistry.AddAsync(device, ct);

        var wrongTokenBytes = RandomNumberGenerator.GetBytes(32);
        var wrongToken = Base64Url.EncodeToString(wrongTokenBytes);

        var sp = BuildServiceProvider();
        var ctx = BuildContext($"Bearer {wrongToken}", sp);
        var authService = sp.GetRequiredService<IAuthenticationService>();

        var result = await authService.AuthenticateAsync(ctx, DeviceTokenAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);  // Fail, not NoResult
    }

    [Fact]
    public async Task Token_for_empty_registry_returns_fail()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64Url.EncodeToString(tokenBytes);

        var sp = BuildServiceProvider();
        var ctx = BuildContext($"Bearer {rawToken}", sp);
        var authService = sp.GetRequiredService<IAuthenticationService>();

        var result = await authService.AuthenticateAsync(ctx, DeviceTokenAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    // ── Missing / malformed Authorization header ─────────────────────────────

    [Fact]
    public async Task No_authorization_header_returns_no_result()
    {
        var sp = BuildServiceProvider();
        var ctx = BuildContext(null, sp);
        var authService = sp.GetRequiredService<IAuthenticationService>();

        var result = await authService.AuthenticateAsync(ctx, DeviceTokenAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);  // NoResult, not Fail
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task Basic_auth_header_returns_no_result()
    {
        var sp = BuildServiceProvider();
        var ctx = BuildContext("Basic dXNlcjpwYXNz", sp);
        var authService = sp.GetRequiredService<IAuthenticationService>();

        var result = await authService.AuthenticateAsync(ctx, DeviceTokenAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);  // NoResult, not Fail
    }

    [Fact]
    public async Task Bearer_header_with_empty_token_returns_no_result()
    {
        var sp = BuildServiceProvider();
        var ctx = BuildContext("Bearer ", sp);
        var authService = sp.GetRequiredService<IAuthenticationService>();

        var result = await authService.AuthenticateAsync(ctx, DeviceTokenAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);  // NoResult, not Fail
    }
}
