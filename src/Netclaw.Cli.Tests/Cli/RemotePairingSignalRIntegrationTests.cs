// -----------------------------------------------------------------------
// <copyright file="RemotePairingSignalRIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
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
    private readonly LocalControlPairingProofProtector _proofProtector;
    private readonly LocalControlPairingProofValidator _proofValidator;
    private readonly PairingCoordinator _pairingCoordinator;

    public RemotePairingSignalRIntegrationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        _deviceRegistry = new DeviceRegistry(_paths, TimeProvider.System, NullLogger<DeviceRegistry>.Instance);
        _pairingCodeService = new PairingCodeService(TimeProvider.System);
        _exchangeGuard = new PairingExchangeGuard(TimeProvider.System);
        var provider = SecretsProtection.CreateDataProtectionProvider(_paths);
        _proofProtector = new LocalControlPairingProofProtector(provider);
        _proofValidator = new LocalControlPairingProofValidator(
            _proofProtector,
            TimeProvider.System,
            NullLogger<LocalControlPairingProofValidator>.Instance);
        _pairingCoordinator = new PairingCoordinator(
            _pairingCodeService,
            _deviceRegistry,
            TimeProvider.System,
            NullLogger<PairingCoordinator>.Instance);
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
        await using var app = await CreateAppAsync();
        var httpClient = app.GetTestClient();
        var proof = _proofProtector.CreateProof(TimeProvider.System.GetUtcNow());
        var codeResponse = await httpClient.PostAsJsonAsync(
            "/api/local-control/v1/pairing-code",
            new { proof },
            ct);
        codeResponse.EnsureSuccessStatusCode();
        var codeResult = await codeResponse.Content.ReadFromJsonAsync<PairingCodeResultDto>(ct);
        Assert.NotNull(codeResult);

        var exchangeResponse = await httpClient.PostAsJsonAsync(
            "/api/pair/exchange",
            new { code = codeResult.FormattedCode, deviceName = "remote-laptop" },
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
        builder.Services.AddSingleton(_proofProtector);
        builder.Services.AddSingleton(_proofValidator);
        builder.Services.AddSingleton(_pairingCoordinator);
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("pairing-exchange", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    }));
            options.AddPolicy(PairingEndpointRouteBuilderExtensions.LocalControlRateLimitPolicy, context =>
                RateLimitPartition.GetNoLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapPairingEndpoints();

        app.MapHub<AuthenticatedHub>("/hub/session");
        await app.StartAsync();
        return app;
    }

    private sealed record ExchangeResponse(string Token);
}
