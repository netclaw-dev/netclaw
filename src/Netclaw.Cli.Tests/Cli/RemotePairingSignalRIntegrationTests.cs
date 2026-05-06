// -----------------------------------------------------------------------
// <copyright file="RemotePairingSignalRIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class RemotePairingSignalRIntegrationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly DeviceRegistry _deviceRegistry;
    private readonly PairingCodeService _pairingCodeService;
    private readonly PairingExchangeGuard _exchangeGuard;

    public RemotePairingSignalRIntegrationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        _deviceRegistry = new DeviceRegistry(_paths, TimeProvider.System, NullLogger<DeviceRegistry>.Instance);
        _pairingCodeService = new PairingCodeService(TimeProvider.System);
        _exchangeGuard = new PairingExchangeGuard(TimeProvider.System);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Authorize]
    private sealed class AuthenticatedHub : Hub
    {
        public string GetSenderId()
            => Context.User?.FindFirst(NetclawClaimTypes.DeviceId)?.Value ?? "unknown";

        public string GetTransportAuthenticity()
            => Context.User?.FindFirst(NetclawClaimTypes.TransportAuthenticity)?.Value ?? "unknown";
    }

    [Fact]
    public async Task PairingExchange_TokenAuthenticatesRealSignalRConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _) = _pairingCodeService.GenerateCode();

        await using var app = await CreateAppAsync();
        var httpClient = app.GetTestClient();

        var exchangeResponse = await httpClient.PostAsJsonAsync(
            "/api/pair/exchange",
            new { code, deviceName = "remote-laptop" },
            ct);
        exchangeResponse.EnsureSuccessStatusCode();

        var tokenPayload = await exchangeResponse.Content.ReadFromJsonAsync<ExchangeResponse>(ct);
        Assert.NotNull(tokenPayload);
        Assert.False(string.IsNullOrWhiteSpace(tokenPayload!.Token));

        var server = app.GetTestServer();
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hub/session", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(tokenPayload.Token);
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await connection.StartAsync(ct);

        var senderId = await connection.InvokeAsync<string>("GetSenderId", ct);
        var transportAuthenticity = await connection.InvokeAsync<string>("GetTransportAuthenticity", ct);

        Assert.Equal("remote-laptop", senderId);
        Assert.Equal(nameof(TransportAuthenticity.Verified), transportAuthenticity);
    }

    private async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton(_deviceRegistry);
        builder.Services.AddSingleton(_pairingCodeService);
        builder.Services.AddSingleton(_exchangeGuard);
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

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
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var tokenBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var rawToken = System.Buffers.Text.Base64Url.EncodeToString(tokenBytes);

            var saltBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
            var tokenHash = PairedDevice.ComputeTokenHash(rawToken, saltHex);

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

        app.MapHub<AuthenticatedHub>("/hub/session");
        await app.StartAsync();
        return app;
    }

    private sealed record ExchangeResponse(string Token);

    private sealed record PairingCodeExchangeRequest(string Code, string DeviceName);
}
