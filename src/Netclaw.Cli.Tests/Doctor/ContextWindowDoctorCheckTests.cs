// -----------------------------------------------------------------------
// <copyright file="ContextWindowDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class ContextWindowDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ContextWindowDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task NoConfigFile_ReturnsWarning()
    {
        var check = new ContextWindowDoctorCheck(_paths, CreateOfflineDaemonApi());
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // TryReadConfig returns a Warning when the config file doesn't exist
        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Config file not found", result.Message);
    }

    [Fact]
    public async Task NoModelsMainSection_ReturnsWarning()
    {
        WriteConfig(new { configVersion = 1 });
        var check = new ContextWindowDoctorCheck(_paths, CreateOfflineDaemonApi());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("No Models.Main section", result.Message);
    }

    [Fact]
    public async Task ExplicitContextWindow_Passes()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Models = new { Main = new { ModelId = "test-model", Provider = "local-ollama", ContextWindow = 131072 } }
        });
        var check = new ContextWindowDoctorCheck(_paths, CreateOfflineDaemonApi());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("131,072", result.Message);
    }

    [Fact]
    public async Task InvalidContextWindow_ReturnsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Models = new { Main = new { ModelId = "test-model", Provider = "local-ollama", ContextWindow = -1 } }
        });
        var check = new ContextWindowDoctorCheck(_paths, CreateOfflineDaemonApi());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task NoExplicitContextWindow_DaemonReportsValue_Passes()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Models = new { Main = new { ModelId = "qwen3:30b", Provider = "local-ollama" } }
        });

        var daemonApi = CreateDaemonApi(_ => JsonResponse(BuildStatusResponse(262144)));

        var check = new ContextWindowDoctorCheck(_paths, daemonApi);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("262,144", result.Message);
        Assert.Contains("from running daemon", result.Message);
    }

    [Fact]
    public async Task NoExplicitContextWindow_DaemonOffline_ProviderUnreachable_ReturnsWarning()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Models = new { Main = new { ModelId = "qwen3:30b", Provider = "local-ollama" } }
        });

        var check = new ContextWindowDoctorCheck(_paths, CreateOfflineDaemonApi());
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Could not detect context window", result.Message);
        Assert.Contains("daemon:", result.Message);
    }

    private void WriteConfig(object config)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static DaemonApi CreateOfflineDaemonApi()
        => CreateDaemonApi(_ => throw new HttpRequestException("daemon offline"));

    private static DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-ctx-test-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();
        return new DaemonApi(new StubHttpClientFactory(handler), configuration, paths);
    }

    private static object BuildStatusResponse(int contextWindow) => new
    {
        Overall = "healthy",
        Build = new { Version = "1.0.0", CommitHash = "abc123", BuildTimestamp = "2026-01-01T00:00:00Z" },
        Process = new { Pid = 1234, StartedAtUtc = "2026-01-01T00:00:00Z", UptimeSeconds = 3600 },
        Connectors = Array.Empty<object>(),
        Persistence = new { Provider = "sqlite" },
        Telemetry = new { Enabled = false },
        Model = new
        {
            ModelId = "qwen3:30b",
            Provider = "openai-compatible",
            ContextWindow = contextWindow,
            InputModalities = "Text",
            OutputModalities = "Text"
        }
    };

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(handler));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }
}
