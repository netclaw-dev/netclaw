using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonApiAuthenticationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public DaemonApiAuthenticationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-daemon-api-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", null);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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
                return JsonResponse(Array.Empty<object>());
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
        HttpRequestMessage? capturedRequest = null;

        var api = CreateDaemonApi(
            "http://127.0.0.1:5199",
            request =>
            {
                capturedRequest = request;
                return JsonResponse(Array.Empty<object>());
            });

        var devices = await api.ListPairedDevicesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest!.Headers.Authorization);
        Assert.Empty(devices);
    }

    [Fact]
    public void ResolveEndpoint_EnvironmentOverride_WinsOverClientConfig()
    {
        ClientConfigFile.WriteEndpoint(_paths, "http://192.168.1.50:5199");
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", "http://override-host:6000/");

        var endpoint = DaemonApi.ResolveEndpoint(_paths);

        Assert.Equal("http://override-host:6000", endpoint);
    }

    private DaemonApi CreateDaemonApi(string endpoint, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        ClientConfigFile.WriteEndpoint(_paths, endpoint);
        var configuration = new ConfigurationBuilder().Build();

        return new DaemonApi(new StubHttpClientFactory(handler), configuration, _paths);
    }

    private void WriteDeviceToken(string token)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["DeviceToken"] = token
        });

        File.WriteAllText(_paths.SecretsPath, json);
    }

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(_handler));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}
