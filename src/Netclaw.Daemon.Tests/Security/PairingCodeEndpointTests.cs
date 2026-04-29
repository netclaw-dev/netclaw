// -----------------------------------------------------------------------
// <copyright file="PairingCodeEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
/// Integration tests for the <c>GET /api/pair/devices</c> and
/// <c>DELETE /api/pair/devices/{name}</c> endpoints.
///
/// Uses a minimal <see cref="WebApplication"/> with the same auth pipeline as production,
/// verifying that the endpoints are reachable from authenticated (loopback) connections
/// and blocked from unauthenticated connections.
/// </summary>
public sealed class PairingCodeEndpointTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly FakeTimeProvider _time;
    private readonly DeviceRegistry _registry;

    public PairingCodeEndpointTests()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        _registry = new DeviceRegistry(new NetclawPaths(_dir.Path), _time, NullLogger<DeviceRegistry>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    private async Task<WebApplication> CreateAppAsync(bool spoofLoopback = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_registry);
        builder.Services.AddNetclawAuthSchemes();
        builder.Services.AddAuthorization();

        var app = builder.Build();

        if (spoofLoopback)
        {
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                await next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/api/pair/devices", async (DeviceRegistry deviceRegistry, CancellationToken ct) =>
        {
            var devices = await deviceRegistry.ListAsync(ct);
            var sanitized = devices.Select(d => new PairedDeviceInfoDto(d.Name, d.CreatedAt, d.LastUsedAt));
            return Results.Ok(sanitized);
        }).RequireAuthorization();

        app.MapDelete("/api/pair/devices/{name}", async (string name, DeviceRegistry deviceRegistry, CancellationToken ct) =>
        {
            var removed = await deviceRegistry.RemoveAsync(name, ct);
            return removed
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Device '{name}' not found." });
        }).RequireAuthorization();

        await app.StartAsync();
        return app;
    }

    private PairedDevice MakeDevice(string name)
    {
        return DeviceTestHelpers.MakeDevice(name, _time.GetUtcNow()).Device;
    }

    [Fact]
    public async Task List_devices_returns_401_for_unauthenticated_request()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/pair/devices", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_devices_returns_empty_list_from_loopback_when_no_devices_registered()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/pair/devices", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var devices = await response.Content.ReadFromJsonAsync<List<PairedDeviceInfoDto>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(devices);
        Assert.Empty(devices);
    }

    [Fact]
    public async Task List_devices_returns_sanitized_device_list_from_loopback()
    {
        var ct = TestContext.Current.CancellationToken;
        await _registry.AddAsync(MakeDevice("laptop"), ct);
        await _registry.AddAsync(MakeDevice("phone"), ct);

        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/pair/devices", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var devices = await response.Content.ReadFromJsonAsync<List<PairedDeviceInfoDto>>(ct);
        Assert.NotNull(devices);
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Name == "laptop");
        Assert.Contains(devices, d => d.Name == "phone");
    }

    [Fact]
    public async Task Revoke_device_returns_401_for_unauthenticated_request()
    {
        var ct = TestContext.Current.CancellationToken;
        await _registry.AddAsync(MakeDevice("laptop"), ct);

        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync("/api/pair/devices/laptop", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_device_removes_it_and_returns_204_from_loopback()
    {
        var ct = TestContext.Current.CancellationToken;
        await _registry.AddAsync(MakeDevice("laptop"), ct);

        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync("/api/pair/devices/laptop", ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var remaining = await _registry.ListAsync(ct);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Revoke_device_returns_404_when_device_not_found()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync(
            "/api/pair/devices/nonexistent", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
