// -----------------------------------------------------------------------
// <copyright file="ContextWindowDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli;
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
        var check = CreateCheck(CreateOfflineDaemonApi());
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Equal(CliConfigPreflight.MissingConfigMessage, result.Message);
    }

    [Fact]
    public async Task NoModelsMainSection_ReturnsWarning()
    {
        WriteConfig(new { configVersion = 1 });
        var check = CreateCheck(CreateOfflineDaemonApi());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Context window unavailable", result.Message);
        Assert.Contains("Models:Main missing", result.Message);
        Assert.Contains("netclaw init", result.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("32,768", result.Message);
    }

    [Fact]
    public async Task MainModelWithoutProvider_ReturnsUnavailableWithoutDefaultProvider()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Models = new { Main = new { ModelId = "qwen3:30b" } }
        });
        var check = CreateCheck(CreateOfflineDaemonApi());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Context window unavailable", result.Message);
        Assert.Contains("netclaw init", result.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("local-ollama", result.Message);
    }

    [Fact]
    public async Task MissingModelWithProvider_ReferencesModelCommand()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Providers = ProviderConfig("local-ollama", "ollama")
        });
        var check = CreateCheck(CreateOfflineDaemonApi());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("netclaw model", result.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitContextWindow_Passes()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Providers = ProviderConfig("local-ollama", "ollama"),
            Models = new { Main = new { ModelId = "test-model", Provider = "local-ollama", ContextWindow = 131072 } }
        });
        var check = CreateCheck(CreateOfflineDaemonApi());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("131,072", result.Message);
    }

    [Fact]
    public async Task ExplicitContextWindow_DaemonReportsDifferentValue_ReturnsWarning()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Providers = ProviderConfig("openai-codex", "openai"),
            Models = new { Main = new { ModelId = "gpt-5.3-codex", Provider = "openai-codex", ContextWindow = 32768 } }
        });

        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(BuildStatusResponse(400000)));
        var check = CreateCheck(daemonApi);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("32,768", result.Message);
        Assert.Contains("400,000", result.Message);
        Assert.Contains("precedence", result.Message);
    }

    [Fact]
    public async Task ExplicitContextWindow_DaemonReportsSameValue_Passes()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Providers = ProviderConfig("local-ollama", "ollama"),
            Models = new { Main = new { ModelId = "qwen3:30b", Provider = "local-ollama", ContextWindow = 262144 } }
        });

        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(BuildStatusResponse(262144)));
        var check = CreateCheck(daemonApi);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("explicitly set", result.Message);
    }

    [Fact]
    public async Task InvalidContextWindow_ReturnsError()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Providers = ProviderConfig("local-ollama", "ollama"),
            Models = new { Main = new { ModelId = "test-model", Provider = "local-ollama", ContextWindow = -1 } }
        });
        var check = CreateCheck(CreateOfflineDaemonApi());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task NoExplicitContextWindow_DaemonReportsValue_Passes()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Providers = ProviderConfig("local-ollama", "ollama"),
            Models = new { Main = new { ModelId = "qwen3:30b", Provider = "local-ollama" } }
        });

        var daemonApi = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(BuildStatusResponse(262144)));
        var check = CreateCheck(daemonApi);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("262,144", result.Message);
        Assert.Contains("from running daemon", result.Message);
    }

    [Fact]
    public async Task NoExplicitContextWindow_DaemonOffline_ProviderProbeSucceeds_Passes()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Providers = ProviderConfig("local-ollama", "ollama"),
            Models = new { Main = new { ModelId = "qwen3:30b", Provider = "local-ollama" } }
        });

        var check = CreateCheck(
            CreateOfflineDaemonApi(),
            probeResult: 131072);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("131,072", result.Message);
        Assert.Contains("from provider", result.Message);
    }

    [Fact]
    public async Task NoExplicitContextWindow_DaemonOffline_ProviderProbeThrows_ReturnsWarning()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Providers = ProviderConfig("local-ollama", "ollama"),
            Models = new { Main = new { ModelId = "qwen3:30b", Provider = "local-ollama" } }
        });

        var check = CreateCheck(
            CreateOfflineDaemonApi(),
            probeException: new HttpRequestException("connection refused"));

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Could not detect context window", result.Message);
        Assert.Contains("daemon:", result.Message);
        Assert.Contains("provider probe failed: HttpRequestException", result.Message);
    }

    [Fact]
    public async Task NoExplicitContextWindow_DaemonOffline_ProviderReturnsNull_ReturnsWarning()
    {
        WriteConfig(new
        {
            configVersion = 1,
            Providers = ProviderConfig("local-ollama", "ollama"),
            Models = new { Main = new { ModelId = "qwen3:30b", Provider = "local-ollama" } }
        });

        var check = CreateCheck(
            CreateOfflineDaemonApi(),
            probeResult: null);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Could not detect context window", result.Message);
        Assert.Contains("provider returned no context window", result.Message);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ContextWindowDoctorCheck CreateCheck(
        DaemonApi daemonApi,
        int? probeResult = null,
        Exception? probeException = null)
    {
        Task<int?> FakeProbe(string _, string __, CancellationToken ___)
        {
            if (probeException is not null) throw probeException;
            return Task.FromResult(probeResult);
        }

        return new ContextWindowDoctorCheck(_paths, daemonApi, BuildConfiguration(), FakeProbe);
    }

    private IConfigurationRoot BuildConfiguration() => new ConfigurationBuilder()
        .AddJsonFile(_paths.NetclawConfigPath, optional: true, reloadOnChange: false)
        .AddJsonFile(_paths.SecretsPath, optional: true, reloadOnChange: false)
        .Build();

    private static Dictionary<string, object> ProviderConfig(string name, string type) => new()
    {
        [name] = new { Type = type }
    };

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
        return new DaemonApi(new FakeHttpClientFactory(handler), configuration, paths);
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

}
