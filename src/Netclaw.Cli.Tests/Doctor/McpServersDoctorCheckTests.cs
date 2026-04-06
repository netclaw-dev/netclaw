using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class McpServersDoctorCheckTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public McpServersDoctorCheckTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task NoConfigFile_Passes()
    {
        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task NoMcpServersSection_Passes()
    {
        WriteConfig(new { configVersion = 1 });
        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => JsonResponse(new { })));

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("No MCP servers", result.Message);
    }

    [Fact]
    public async Task ValidStdioServer_UnreachableEndpoint_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                memorizer = new
                {
                    Transport = "stdio",
                    Command = "npx",
                    Arguments = new[] { "-y", "@memorizer/mcp-server" },
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => throw new HttpRequestException("daemon offline")));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // Single enabled server that can't connect → Error
        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("unreachable", result.Message);
    }

    [Fact]
    public async Task StdioServerMissingCommand_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                broken = new
                {
                    Transport = "stdio",
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("requires 'Command'", result.Message);
    }

    [Fact]
    public async Task HttpServerMissingUrl_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                broken = new
                {
                    Transport = "http",
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("requires 'Url'", result.Message);
    }

    [Fact]
    public async Task InvalidTransport_ReportsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                broken = new
                {
                    Transport = "grpc",
                    Enabled = true
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("invalid transport", result.Message);
    }

    [Fact]
    public async Task DisabledServer_SkipsProbe()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                disabled_one = new { Transport = "stdio", Command = "npx", Enabled = false }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => JsonResponse(new { })));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // Only disabled servers → all pass (no enabled servers to fail)
        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("disabled", result.Message);
    }

    [Fact]
    public async Task DaemonReportedAuthFailure_ReturnsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                notion = new
                {
                    Transport = "http",
                    Url = "https://mcp.example.com",
                    Enabled = true,
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => JsonResponse(new
        {
            notion = new
            {
                state = "AuthFailed",
                toolCount = 0,
                error = "Authentication rejected by server (401 Unauthorized). Run: netclaw mcp auth notion"
            }
        })));

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("auth failed", result.Message);
        Assert.Contains("netclaw mcp auth", result.Remediation);
    }

    [Fact]
    public async Task DaemonReportedAwaitingAuth_ReturnsWarning()
    {
        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                textforge = new
                {
                    Transport = "http",
                    Url = "https://mcp.example.com",
                    Enabled = true,
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => JsonResponse(new
        {
            textforge = new
            {
                state = "AwaitingAuth",
                toolCount = 0,
                error = "OAuth authorization required. Run: netclaw mcp auth textforge"
            }
        })));

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("awaiting auth", result.Message);
    }

    [Fact]
    public async Task OfflineOAuthProbe_DoesNotClaimAuthFailure()
    {
        using var server = new UnauthorizedHttpServer();

        WriteConfig(new
        {
            configVersion = 1,
            McpServers = new
            {
                notion = new
                {
                    Transport = "http",
                    Url = server.Url,
                    Enabled = true,
                    OAuthClientId = "client-id"
                }
            }
        });

        var check = new McpServersDoctorCheck(_paths, CreateDaemonApi(_ => throw new HttpRequestException("daemon offline")));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("auth cannot be verified offline", result.Message);
        Assert.DoesNotContain("auth failed", result.Message);
    }

    private void WriteConfig(object config)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-daemon-api-test-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();

        return new DaemonApi(new StubHttpClientFactory(handler), configuration, paths);
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

    private sealed class UnauthorizedHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serverTask;

        public UnauthorizedHttpServer()
        {
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/mcp";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serverTask = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public void Dispose()
        {
            _listener.Close();

            var task = _serverTask;
            if (task.IsFaulted)
            {
                var exception = task.Exception?.GetBaseException();
                if (exception is not HttpListenerException and not ObjectDisposedException)
                    throw exception ?? task.Exception!;
            }
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                context.Response.Close();
            }
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
