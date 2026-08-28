// -----------------------------------------------------------------------
// <copyright file="DaemonApiAuthenticationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
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
    public async Task RequestPairingCode_uses_local_daemon_config_without_bearer_token()
    {
        ClientConfigFile.WriteEndpoint(_paths, "https://remote.example.test");
        File.WriteAllText(
            _paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Daemon\":{\"Host\":\"0.0.0.0\",\"Port\":6200,\"ExposureMode\":\"reverse-proxy\"}}");
        WriteDeviceToken("remote-device-token");
        HttpRequestMessage? capturedRequest = null;
        var expiresAt = new DateTimeOffset(2026, 8, 28, 12, 5, 0, TimeSpan.Zero);
        var factory = new FakeHttpClientFactory(
            request =>
            {
                capturedRequest = request;
                return FakeHttpMessageHandler.JsonResponse(
                    new PairingCodeResultDto("ABCD-EFGH", expiresAt));
            });
        var api = new DaemonApi(factory, new ConfigurationBuilder().Build(), _paths);

        var result = await api.RequestPairingCodeAsync(
            "host-proof",
            TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Equal(DaemonApi.LocalControlHttpClientName, factory.LastClientName);
        Assert.Equal(
            "http://127.0.0.1:6200/api/local-control/v1/pairing-code",
            capturedRequest.RequestUri!.AbsoluteUri);
        Assert.Null(capturedRequest.Headers.Authorization);
        Assert.Equal("https://remote.example.test", api.Endpoint);
        var requestJson = await capturedRequest.Content!.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal("host-proof", requestJson.GetProperty("proof").GetString());
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("ABCD-EFGH", result.Result!.FormattedCode);
        Assert.Equal(expiresAt, result.Result.ExpiresAt);
    }

    [Fact]
    public async Task RequestPairingCode_does_not_follow_a_redirect()
    {
        var requestCount = 0;
        var factory = new FakeHttpClientFactory(request =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://remote.example.test/capture") },
            };
        });
        var api = new DaemonApi(factory, new ConfigurationBuilder().Build(), _paths);

        var result = await api.RequestPairingCodeAsync(
            "host-proof",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, result.StatusCode);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public void Local_control_http_handler_disables_redirects_and_proxies()
    {
        using var handler = DaemonApi.CreateLocalControlHttpHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task Local_control_http_handler_does_not_send_proof_to_redirect_target()
    {
        await using var target = new OneRequestHttpServer(HttpStatusCode.OK);
        await using var source = new OneRequestHttpServer(
            HttpStatusCode.TemporaryRedirect,
            target.Endpoint);
        using var handler = DaemonApi.CreateLocalControlHttpHandler();
        using var client = new HttpClient(handler);

        using var response = await client.PostAsJsonAsync(
            source.Endpoint,
            new { proof = "host-proof" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.True(source.ReceivedRequest);
        Assert.False(target.ReceivedRequest);
    }

    [Fact]
    public async Task Local_control_http_handler_does_not_use_configured_proxy()
    {
        await using var destination = new OneRequestHttpServer(HttpStatusCode.OK);
        var proxy = new RecordingWebProxy();
        using var handler = DaemonApi.CreateLocalControlHttpHandler();
        handler.Proxy = proxy;
        using var client = new HttpClient(handler);

        using var response = await client.PostAsJsonAsync(
            destination.Endpoint,
            new { proof = "host-proof" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(destination.ReceivedRequest);
        Assert.Equal(0, proxy.CallCount);
    }

    [Fact]
    public async Task RequestPairingCode_returns_version_error_for_mixed_version_guidance()
    {
        var api = CreateDaemonApi(
            "http://127.0.0.1:5199",
            _ => FakeHttpMessageHandler.JsonResponse(
                new { error = "unsupported_protocol_version" },
                HttpStatusCode.BadRequest));

        var result = await api.RequestPairingCodeAsync(
            "host-proof",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Null(result.Result);
        Assert.Equal("unsupported_protocol_version", result.Error);
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
    public void ResolveLocalControlEndpoint_ignores_remote_client_state()
    {
        ClientConfigFile.WriteEndpoint(_paths, "https://remote.example.test");
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", "https://override.example.test");
        File.WriteAllText(
            _paths.NetclawConfigPath,
            "{\"configVersion\":1,\"Daemon\":{\"Host\":\"0.0.0.0\",\"Port\":6200}}");

        var endpoint = DaemonApi.ResolveLocalControlEndpoint(_paths);

        Assert.Equal("http://127.0.0.1:6200", endpoint);
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

    private sealed class RecordingWebProxy : IWebProxy
    {
        public int CallCount { get; private set; }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            CallCount++;
            return new Uri("http://127.0.0.1:1");
        }

        public bool IsBypassed(Uri host)
        {
            CallCount++;
            return false;
        }
    }

    private sealed class OneRequestHttpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serveTask;
        private readonly HttpStatusCode _statusCode;
        private readonly Uri? _redirectTarget;
        private int _receivedRequest;

        public OneRequestHttpServer(HttpStatusCode statusCode, Uri? redirectTarget = null)
        {
            _statusCode = statusCode;
            _redirectTarget = redirectTarget;
            Endpoint = new Uri($"http://127.0.0.1:{ReservePort()}/");
            _listener.Prefixes.Add(Endpoint.AbsoluteUri);
            _listener.Start();
            _serveTask = ServeAsync();
        }

        public Uri Endpoint { get; }

        public bool ReceivedRequest => Volatile.Read(ref _receivedRequest) == 1;

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            await _serveTask;
        }

        private async Task ServeAsync()
        {
            try
            {
                var context = await _listener.GetContextAsync();
                Interlocked.Exchange(ref _receivedRequest, 1);
                context.Response.StatusCode = (int)_statusCode;
                if (_redirectTarget is not null)
                    context.Response.RedirectLocation = _redirectTarget.AbsoluteUri;

                var body = Encoding.UTF8.GetBytes("{}");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
            catch (HttpListenerException) when (!_listener.IsListening)
            {
                return;
            }
            catch (ObjectDisposedException) when (!_listener.IsListening)
            {
                return;
            }
        }

        private static int ReservePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

}
