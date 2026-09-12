// -----------------------------------------------------------------------
// <copyright file="RemotePairingSignalRIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Akka.Hosting;
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
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
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
    private readonly PairingExchangeGuard _exchangeGuard;
    private readonly LocalControlPairingProofProtector _proofProtector;
    private readonly LocalControlPairingProofValidator _proofValidator;

    public RemotePairingSignalRIntegrationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        _deviceRegistry = new DeviceRegistry(_paths, TimeProvider.System, NullLogger<DeviceRegistry>.Instance);
        _exchangeGuard = new PairingExchangeGuard(TimeProvider.System);
        var provider = SecretsProtection.CreateDataProtectionProvider(_paths);
        _proofProtector = new LocalControlPairingProofProtector(provider);
        _proofValidator = new LocalControlPairingProofValidator(
            _proofProtector,
            TimeProvider.System,
            NullLogger<LocalControlPairingProofValidator>.Instance);
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

        using var clientDirectory = new DisposableTempDir();
        var clientPaths = new NetclawPaths(clientDirectory.Path);
        clientPaths.EnsureDirectoriesExist();
        using var input = new StringReader(codeResult.FormattedCode + "\nremote-laptop\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await PairCommand.RunAsync(
            ["pair", "http://localhost"], clientPaths, httpClient, input, output, error, TimeProvider.System, ct);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal("http://localhost", ClientConfigFile.ReadEndpoint(clientPaths));
        var secrets = ConfigFileHelper.LoadJsonDict(clientPaths.SecretsPath);
        var storedToken = Assert.IsType<JsonElement>(secrets["DeviceToken"]);
        var token = ConfigFileHelper.DecryptIfEncrypted(clientPaths, storedToken.GetString());
        Assert.False(string.IsNullOrWhiteSpace(token));

        var server = app.GetTestServer();
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hub/session", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
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
        builder.Services.AddSingleton(_exchangeGuard);
        builder.Services.AddSingleton(_proofProtector);
        builder.Services.AddSingleton(_proofValidator);
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR();
        builder.Services.AddAkka($"remote-pairing-tests-{Guid.NewGuid():N}", (akka, _) =>
            akka.WithPairingActor());
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
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

}
