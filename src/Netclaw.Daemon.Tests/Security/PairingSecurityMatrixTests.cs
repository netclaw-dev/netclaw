// -----------------------------------------------------------------------
// <copyright file="PairingSecurityMatrixTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

public sealed class PairingSecurityMatrixTests
{
    [Fact]
    public async Task Pairing_authority_matrix_matches_approved_snapshot()
    {
        var table = new StringBuilder();
        table.AppendLine("| Exposure mode | Bearer credential | Proof | HTTP result | Creates code |");
        table.AppendLine("|---|---|---|---|---|");
        var caseIndex = 0;

        foreach (var mode in Enum.GetValues<ExposureMode>())
        {
            foreach (var credential in new[] { "none", "device", "bootstrap" })
            {
                using var hostDir = new DisposableTempDir();
                using var remoteDir = new DisposableTempDir();
                var time = new FakeTimeProvider(
                    new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
                var paths = new NetclawPaths(hostDir.Path);
                var hostProtector = new LocalControlPairingProofProtector(
                    SecretsProtection.CreateDataProtectionProvider(paths));
                var remoteProtector = new LocalControlPairingProofProtector(
                    SecretsProtection.CreateDataProtectionProvider(new NetclawPaths(remoteDir.Path)));
                var pairingCodeService = new PairingCodeService(time);
                var registry = new DeviceRegistry(paths, time, NullLogger<DeviceRegistry>.Instance);
                string? bearerToken = null;

                if (credential != "none")
                {
                    var device = DeviceTestHelpers.MakeDevice(
                        $"{credential}-{mode}",
                        time.GetUtcNow(),
                        credential == "bootstrap");
                    bearerToken = device.RawToken;
                    await registry.AddAsync(device.Device, TestContext.Current.CancellationToken);
                }

                await using var app = await CreateAppAsync(
                    mode,
                    time,
                    hostProtector,
                    pairingCodeService,
                    registry);
                var client = app.GetTestClient();
                client.DefaultRequestHeaders.Authorization = bearerToken is null
                    ? null
                    : new AuthenticationHeaderValue("Bearer", bearerToken);
                string? priorCode = null;

                foreach (var proofCase in new[]
                         {
                             "valid", "missing", "changed", "cross-home", "stale",
                             "future", "malformed", "unsupported-version", "wrong-operation", "replay"
                         })
                {
                    var proof = CreateProof(
                        proofCase,
                        caseIndex++,
                        hostProtector,
                        remoteProtector,
                        time.GetUtcNow());

                    if (proofCase == "replay")
                    {
                        var first = await PostProofAsync(client, proof);
                        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                        var body = await first.Content.ReadFromJsonAsync<PairingCodeResultDto>(
                            TestContext.Current.CancellationToken);
                        priorCode = Assert.IsType<PairingCodeResultDto>(body).FormattedCode;
                    }

                    var response = await PostProofAsync(client, proof);
                    var createsCode = response.StatusCode == HttpStatusCode.OK;

                    if (createsCode)
                    {
                        var body = await response.Content.ReadFromJsonAsync<PairingCodeResultDto>(
                            TestContext.Current.CancellationToken);
                        priorCode = Assert.IsType<PairingCodeResultDto>(body).FormattedCode;
                    }
                    else if (priorCode is not null)
                    {
                        Assert.True(pairingCodeService.TryConsume(priorCode));
                        priorCode = null;
                    }
                    else
                    {
                        Assert.Null(pairingCodeService.GetPendingExpiry());
                    }

                    var httpResult = response.StatusCode == HttpStatusCode.BadRequest
                        ? "400 version"
                        : ((int)response.StatusCode).ToString();
                    table.AppendLine(
                        $"| {mode} | {credential} | {proofCase} | {httpResult} | {createsCode} |");
                }
            }
        }

        var settings = new VerifySettings();
        settings.DisableScrubbers();
        await Verifier.Verify(table.ToString(), "md", settings);
    }

    private static async Task<WebApplication> CreateAppAsync(
        ExposureMode mode,
        TimeProvider timeProvider,
        LocalControlPairingProofProtector proofProtector,
        PairingCodeService pairingCodeService,
        DeviceRegistry registry)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddSingleton(proofProtector);
        builder.Services.AddSingleton(pairingCodeService);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<PairingExchangeGuard>();
        builder.Services.AddSingleton<LocalControlPairingProofValidator>();
        builder.Services.AddSingleton<PairingCoordinator>();
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig { ExposureMode = mode });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("pairing-exchange", context =>
                RateLimitPartition.GetNoLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapPairingEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static Task<HttpResponseMessage> PostProofAsync(HttpClient client, string proof)
        => client.PostAsJsonAsync(
            "/api/local-control/v1/pairing-code",
            new { proof },
            TestContext.Current.CancellationToken);

    private static string CreateProof(
        string proofCase,
        int caseIndex,
        LocalControlPairingProofProtector hostProtector,
        LocalControlPairingProofProtector remoteProtector,
        DateTimeOffset now)
    {
        var nonce = Convert.ToHexString(BitConverter.GetBytes(caseIndex).Concat(new byte[12]).ToArray());
        var payload = new LocalControlPairingProofPayload(
            LocalControlPairingProofProtector.CurrentVersion,
            LocalControlPairingProofProtector.GeneratePairingCodeOperation,
            now,
            nonce);

        return proofCase switch
        {
            "missing" => "",
            "malformed" => "not-a-proof",
            "changed" => ChangeFirstCharacter(hostProtector.ProtectPayload(payload)),
            "cross-home" => remoteProtector.ProtectPayload(payload),
            "stale" => hostProtector.ProtectPayload(payload with { IssuedAt = now.AddSeconds(-31) }),
            "future" => hostProtector.ProtectPayload(payload with { IssuedAt = now.AddSeconds(6) }),
            "unsupported-version" => hostProtector.ProtectPayload(payload with { Version = 2 }),
            "wrong-operation" => hostProtector.ProtectPayload(payload with { Operation = 2 }),
            _ => hostProtector.ProtectPayload(payload),
        };
    }

    private static string ChangeFirstCharacter(string proof)
        => (proof[0] == 'A' ? "B" : "A") + proof[1..];
}
