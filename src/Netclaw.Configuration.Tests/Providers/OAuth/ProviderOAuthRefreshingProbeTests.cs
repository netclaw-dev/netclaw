// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthRefreshingProbeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public sealed class ProviderOAuthRefreshingProbeTests
{
    [Fact]
    public async Task ProbeConfiguredAsync_ExpiredOAuthToken_RefreshesPersistsAndDelegatesWithFreshToken()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        WriteProviderConfig(paths, "test-provider");

        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var descriptor = new TestOAuthDescriptor();
        var probe = CreateProbe(paths, time, descriptor, new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.JsonResponse(new
            {
                access_token = "access-new",
                refresh_token = "refresh-new",
                expires_in = 3600,
            })));
        var entry = ExpiredEntry(now);

        var result = await probe.ProbeConfiguredAsync(
            "test-provider",
            entry,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("access-new", descriptor.ProbedAccessToken);
        Assert.Equal("access-new", entry.OAuthAccessToken!.Value);
        Assert.Equal("refresh-new", entry.OAuthRefreshToken!.Value);
        Assert.Equal(now.AddHours(1), entry.OAuthTokenExpiry);

        using var secretsDoc = JsonDocument.Parse(File.ReadAllText(paths.SecretsPath));
        var secretProvider = secretsDoc.RootElement.GetProperty("Providers").GetProperty("test-provider");
        Assert.Equal("access-new", secretProvider.GetProperty("OAuthAccessToken").GetString());
        Assert.Equal("refresh-new", secretProvider.GetProperty("OAuthRefreshToken").GetString());
    }

    [Fact]
    public async Task ProbeAsync_TemporaryEntry_DoesNotRefreshOrPersist()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var descriptor = new TestOAuthDescriptor();
        var probe = CreateProbe(paths, time, descriptor, new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("refresh should not run for temporary probes")));
        var entry = ExpiredEntry(now);

        var result = await probe.ProbeAsync(entry, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("access-old", descriptor.ProbedAccessToken);
        Assert.Equal("access-old", entry.OAuthAccessToken!.Value);
        Assert.False(File.Exists(paths.SecretsPath));
    }

    [Fact]
    public async Task ProbeConfiguredAsync_InvalidRefreshToken_ReturnsFailureBeforeDelegateProbe()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var descriptor = new TestOAuthDescriptor();
        var probe = CreateProbe(paths, time, descriptor, new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.JsonResponse(new { error = "invalid_grant" }, HttpStatusCode.BadRequest)));

        var result = await probe.ProbeConfiguredAsync(
            "test-provider",
            ExpiredEntry(now),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("provider fix test-provider", result.ErrorMessage);
        Assert.Null(descriptor.ProbedAccessToken);
    }

    private static ProviderOAuthRefreshingProbe CreateProbe(
        NetclawPaths paths,
        FakeTimeProvider time,
        TestOAuthDescriptor descriptor,
        FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var factory = new DeviceFlowServiceFactory(
            new OAuthDeviceFlowService(httpClient, time),
            new OpenAiDeviceFlowService(httpClient, time));
        var refreshService = new ProviderOAuthTokenRefreshService(paths, factory, timeProvider: time);
        return new ProviderOAuthRefreshingProbe(new ProviderDescriptorRegistry([descriptor]), refreshService);
    }

    private static ProviderEntry ExpiredEntry(DateTimeOffset now) => new()
    {
        Type = "test-oauth",
        AuthMethod = AuthMethod.OAuthDevice,
        OAuthAccessToken = new SensitiveString("access-old"),
        OAuthRefreshToken = new SensitiveString("refresh-old"),
        OAuthTokenExpiry = now.AddMinutes(-1),
    };

    private static void WriteProviderConfig(NetclawPaths paths, string providerName)
    {
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, $$"""
            {
              "configVersion": 1,
              "Providers": {
                "{{providerName}}": {
                  "Type": "test-oauth",
                  "AuthMethod": "OAuthDevice"
                }
              }
            }
            """);
    }

    private sealed class TestOAuthDescriptor : IProviderDescriptor
    {
        public string? ProbedAccessToken { get; private set; }
        public string TypeKey => "test-oauth";
        public string DisplayName => "Test OAuth";
        public string DefaultEndpoint => "https://api.example.com";
        public string ModelListingPath => "/models";
        public IProviderAuth Auth { get; } = new OAuthAuth
        {
            SupportedAuthMethods = [AuthMethod.OAuthDevice],
            TokenEndpoint = new Uri("https://auth.example.com/oauth/token"),
            DeviceEndpoint = new Uri("https://auth.example.com/login/device/code"),
            ClientId = "client-id",
        };

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        {
            ProbedAccessToken = entry.OAuthAccessToken?.Value;
            return Task.FromResult(new ProviderProbeResult(true, null,
                [new DiscoveredModel { ModelId = new ModelId("model-a") }]));
        }
    }
}
