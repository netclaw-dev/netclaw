// -----------------------------------------------------------------------
// <copyright file="ContextWindowResolutionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

public sealed class ContextWindowResolutionTests
{
    [Fact]
    public void ConfiguredValue_ReturnedWithoutDaemonQuery()
    {
        var daemon = CreateDaemonApi(_ => throw new HttpRequestException("should not be called"));

        var result = ContextWindowResolution.Resolve(131_072, daemon, "test-model");

        Assert.Equal(131_072, result);
    }

    [Fact]
    public void NullConfig_DaemonReturnsContextWindow_ReturnsDaemonValue()
    {
        var daemon = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(BuildStatusResponse(262_144)));

        var result = ContextWindowResolution.Resolve(null, daemon, "qwen3:30b");

        Assert.Equal(262_144, result);
    }

    [Fact]
    public void NullConfig_DaemonReturnsZeroContextWindow_Throws()
    {
        var daemon = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(BuildStatusResponse(0)));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ContextWindowResolution.Resolve(null, daemon, "qwen3:30b"));

        Assert.Contains("no context window", ex.Message);
    }

    [Fact]
    public void NullConfig_DaemonReturnsNullModel_Throws()
    {
        var response = new
        {
            Overall = "healthy",
            Build = new { Version = "1.0.0", CommitHash = "abc123", BuildTimestamp = "2026-01-01T00:00:00Z" },
            Process = new { Pid = 1234, StartedAtUtc = "2026-01-01T00:00:00Z", UptimeSeconds = 3600 },
            Connectors = Array.Empty<object>(),
            Persistence = new { Provider = "sqlite" },
            Telemetry = new { Enabled = false },
        };
        var daemon = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(response));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ContextWindowResolution.Resolve(null, daemon, "qwen3:30b"));

        Assert.Contains("no context window", ex.Message);
    }

    [Fact]
    public void NullConfig_DaemonOffline_Throws()
    {
        var daemon = CreateDaemonApi(_ => throw new HttpRequestException("connection refused"));

        Assert.Throws<HttpRequestException>(
            () => ContextWindowResolution.Resolve(null, daemon, "qwen3:30b"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-ctx-res-test-{Guid.NewGuid():N}"));
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

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new FakeHttpMessageHandler(handler));
    }
}
