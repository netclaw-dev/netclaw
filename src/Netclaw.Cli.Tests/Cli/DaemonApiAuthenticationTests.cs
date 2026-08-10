// -----------------------------------------------------------------------
// <copyright file="DaemonApiAuthenticationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonApiAuthenticationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public DaemonApiAuthenticationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", null);
        _dir.Dispose();
    }

    [Fact]
    public async Task ListPairedDevices_RemoteEndpoint_AttachesBearerToken()
    {
        WriteDeviceToken("remote-device-token");
        HttpRequestMessage? capturedRequest = null;

        var api = CreateDaemonApi(
            "http://192.168.1.50:5199",
            request =>
            {
                capturedRequest = request;
                return FakeHttpMessageHandler.JsonResponse(Array.Empty<object>());
            });

        var devices = await api.ListPairedDevicesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("remote-device-token", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Empty(devices);
    }

    [Fact]
    public async Task ListPairedDevices_LoopbackEndpoint_SkipsBearerToken()
    {
        WriteDeviceToken("loopback-device-token");
        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1,\"Daemon\":{\"ExposureMode\":\"local\"}}");
        HttpRequestMessage? capturedRequest = null;

        var api = CreateDaemonApi(
            "http://127.0.0.1:5199",
            request =>
            {
                capturedRequest = request;
                return FakeHttpMessageHandler.JsonResponse(Array.Empty<object>());
            });

        var devices = await api.ListPairedDevicesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest!.Headers.Authorization);
        Assert.Empty(devices);
    }

    [Fact]
    public async Task ListPairedDevices_ReverseProxyLoopbackEndpoint_AttachesBearerToken()
    {
        WriteDeviceToken("reverse-proxy-loopback-device-token");
        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1,\"Daemon\":{\"ExposureMode\":\"reverse-proxy\"}}");
        HttpRequestMessage? capturedRequest = null;

        var api = CreateDaemonApi(
            "http://127.0.0.1:5199",
            request =>
            {
                capturedRequest = request;
                return FakeHttpMessageHandler.JsonResponse(Array.Empty<object>());
            });

        var devices = await api.ListPairedDevicesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("reverse-proxy-loopback-device-token", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Empty(devices);
    }

    [Fact]
    public async Task ListPairedDevices_ReverseProxyWrittenAfterConstruction_AttachesBearerToken()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1,\"Daemon\":{\"ExposureMode\":\"local\"}}");
        HttpRequestMessage? capturedRequest = null;
        var api = CreateDaemonApi(
            "http://127.0.0.1:5199",
            request =>
            {
                capturedRequest = request;
                return FakeHttpMessageHandler.JsonResponse(Array.Empty<object>());
            });

        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1,\"Daemon\":{\"ExposureMode\":\"reverse-proxy\"}}");
        WriteDeviceToken("fresh-bootstrap-device-token");

        var devices = await api.ListPairedDevicesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("fresh-bootstrap-device-token", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Empty(devices);
    }

    [Fact]
    public void ResolveEndpoint_FallsBackToDaemonBindConfig()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1,\"Daemon\":{\"Host\":\"10.0.0.20\",\"Port\":6200}}");

        var endpoint = DaemonApi.ResolveEndpoint(_paths);

        Assert.Equal("http://10.0.0.20:6200", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_NormalizesWildcardBindToLoopback()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1,\"Daemon\":{\"Host\":\"0.0.0.0\",\"Port\":5199}}");

        var endpoint = DaemonApi.ResolveEndpoint(_paths);

        Assert.Equal("http://127.0.0.1:5199", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_FormatsIpv6BindAddress()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1,\"Daemon\":{\"Host\":\"::1\",\"Port\":5199}}");

        var endpoint = DaemonApi.ResolveEndpoint(_paths);

        Assert.Equal("http://[::1]:5199", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_EnvironmentOverride_WinsOverClientConfig()
    {
        ClientConfigFile.WriteEndpoint(_paths, "http://192.168.1.50:5199");
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", "http://override-host:6000/");

        var endpoint = DaemonApi.ResolveEndpoint(_paths);

        Assert.Equal("http://override-host:6000", endpoint);
    }

    [Fact]
    public async Task ProbeReadinessAsync_ReportsHealthyAndParsesGenerationHeader()
    {
        HttpRequestMessage? captured = null;
        var api = CreateDaemonApi("http://127.0.0.1:5199", request =>
        {
            captured = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("X-Netclaw-Generation", "7");
            return response;
        });

        var readiness = await api.ProbeReadinessAsync(TestContext.Current.CancellationToken);

        Assert.True(readiness.Healthy);
        Assert.Equal(7, readiness.Generation);
        Assert.Equal("/api/health/ready", captured!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ProbeReadinessAsync_NoGenerationHeader_ReportsHealthyWithNullGeneration()
    {
        // A pre-#1302 daemon answers 200 without the header — still healthy, generation unknown.
        var api = CreateDaemonApi("http://127.0.0.1:5199",
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        var readiness = await api.ProbeReadinessAsync(TestContext.Current.CancellationToken);

        Assert.True(readiness.Healthy);
        Assert.Null(readiness.Generation);
    }

    [Fact]
    public async Task ProbeReadinessAsync_NonSuccess_ReportsNotHealthy()
    {
        var api = CreateDaemonApi("http://127.0.0.1:5199",
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var readiness = await api.ProbeReadinessAsync(TestContext.Current.CancellationToken);

        Assert.False(readiness.Healthy);
        Assert.Null(readiness.Generation);
    }

    [Fact]
    public async Task ProbeReadinessAsync_ReResolvesEndpoint_AfterDaemonPortChange()
    {
        // NOTE: this test deliberately does NOT use CreateDaemonApi — that helper writes a
        // client-endpoint file (ClientConfigFile.WriteEndpoint), which wins over the Daemon
        // config section in ResolveEndpoint and would FREEZE the endpoint, defeating the
        // re-resolution this test guards. Resolution must fall through to the Daemon section
        // both at construction and at probe time, so no client endpoint / env override is set.
        // Daemon bound :5199 when the client was constructed...
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Daemon\":{\"Host\":\"127.0.0.1\",\"Port\":5199}}");
        HttpRequestMessage? captured = null;
        var api = new DaemonApi(
            new FakeHttpClientFactory(request => { captured = request; return new HttpResponseMessage(HttpStatusCode.OK); }),
            new ConfigurationBuilder().Build(),
            _paths);

        // ...then a Daemon-section change rebinds it to :5300 (as the wizard would write).
        // A frozen endpoint would keep probing :5199; the probe must re-resolve to :5300 (#1304).
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Daemon\":{\"Host\":\"127.0.0.1\",\"Port\":5300}}");

        await api.ProbeReadinessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5300, captured!.RequestUri!.Port);
    }

    private DaemonApi CreateDaemonApi(string endpoint, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        ClientConfigFile.WriteEndpoint(_paths, endpoint);
        var configuration = new ConfigurationBuilder().Build();

        return new DaemonApi(new FakeHttpClientFactory(handler), configuration, _paths);
    }

    private void WriteDeviceToken(string token)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["DeviceToken"] = token
        });

        File.WriteAllText(_paths.SecretsPath, json);
    }

}
