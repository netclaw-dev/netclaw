// -----------------------------------------------------------------------
// <copyright file="ContextWindowResolutionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

public sealed class ContextWindowResolutionTests
{
    [Fact]
    public async Task ResolveRuntime_UsesDaemonRuntimeModelWhenNoContextWindowConfigured()
    {
        var daemon = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(BuildStatusResponse(
            400_000,
            modelId: "gpt-5.3-codex",
            provider: "openai-codex")));

        var result = await ContextWindowResolution.ResolveRuntimeAsync(new ModelReference(), daemon);

        Assert.Equal("gpt-5.3-codex", result.ModelId);
        Assert.Equal("openai-codex", result.Provider);
        Assert.Equal(400_000, result.ContextWindowTokens);
    }

    [Fact]
    public async Task ResolveRuntime_DaemonOnline_OverridesConfiguredContextWindow()
    {
        // The daemon is the source of truth at runtime: its live model wins over a
        // pinned config value when reachable.
        var daemon = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(BuildStatusResponse(
            400_000,
            modelId: "gpt-5.3-codex",
            provider: "openai-codex")));
        var configured = new ModelReference
        {
            Provider = "local-ollama",
            ModelId = "qwen3:30b",
            ContextWindow = 32_768
        };

        var result = await ContextWindowResolution.ResolveRuntimeAsync(configured, daemon);

        Assert.Equal("gpt-5.3-codex", result.ModelId);
        Assert.Equal(400_000, result.ContextWindowTokens);
    }

    [Fact]
    public async Task ResolveRuntime_DaemonOfflineWithConfiguredContextWindow_ReturnsConfiguredModel()
    {
        var daemon = CreateDaemonApi(_ => throw new HttpRequestException("connection refused"));
        var configured = new ModelReference
        {
            Provider = "local-ollama",
            ModelId = "qwen3:30b",
            ContextWindow = 32_768
        };

        var result = await ContextWindowResolution.ResolveRuntimeAsync(configured, daemon);

        Assert.Equal("qwen3:30b", result.ModelId);
        Assert.Equal("local-ollama", result.Provider);
        Assert.Equal(32_768, result.ContextWindowTokens);
    }

    [Fact]
    public async Task ResolveRuntime_EmptyStatusWithConfiguredContextWindow_ReturnsConfigured()
    {
        // Regression: a reachable daemon that returns 200 with a null/empty body
        // must fall back to the configured context window, not crash startup.
        var daemon = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        });
        var configured = new ModelReference
        {
            Provider = "local-ollama",
            ModelId = "qwen3:30b",
            ContextWindow = 32_768
        };

        var result = await ContextWindowResolution.ResolveRuntimeAsync(configured, daemon);

        Assert.Equal("qwen3:30b", result.ModelId);
        Assert.Equal(32_768, result.ContextWindowTokens);
    }

    [Fact]
    public async Task ResolveRuntime_NullModelWithConfiguredContextWindow_ReturnsConfigured()
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
        var configured = new ModelReference
        {
            Provider = "local-ollama",
            ModelId = "qwen3:30b",
            ContextWindow = 32_768
        };

        var result = await ContextWindowResolution.ResolveRuntimeAsync(configured, daemon);

        Assert.Equal(32_768, result.ContextWindowTokens);
    }

    [Fact]
    public async Task ResolveRuntime_NoConfig_DaemonReturnsZeroContextWindow_Throws()
    {
        var daemon = CreateDaemonApi(_ => FakeHttpMessageHandler.JsonResponse(BuildStatusResponse(0)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ContextWindowResolution.ResolveRuntimeAsync(new ModelReference(), daemon));

        Assert.Contains("no context window", ex.Message);
    }

    [Fact]
    public async Task ResolveRuntime_NoConfig_EmptyStatus_Throws()
    {
        var daemon = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ContextWindowResolution.ResolveRuntimeAsync(new ModelReference(), daemon));

        Assert.Contains("empty status", ex.Message);
    }

    [Fact]
    public async Task ResolveRuntime_NoConfig_DaemonOffline_ThrowsDaemonUnavailable()
    {
        var daemon = CreateDaemonApi(_ => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<DaemonUnavailableException>(
            () => ContextWindowResolution.ResolveRuntimeAsync(new ModelReference(), daemon));

        Assert.Contains("Could not reach the Netclaw daemon", ex.Message);
        Assert.Contains("netclaw daemon start", ex.Message);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-ctx-res-test-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();
        return new DaemonApi(new StubHttpClientFactory(handler), configuration, paths);
    }

    private static object BuildStatusResponse(
        int contextWindow,
        string modelId = "qwen3:30b",
        string provider = "openai-compatible") => new
    {
        Overall = "healthy",
        Build = new { Version = "1.0.0", CommitHash = "abc123", BuildTimestamp = "2026-01-01T00:00:00Z" },
        Process = new { Pid = 1234, StartedAtUtc = "2026-01-01T00:00:00Z", UptimeSeconds = 3600 },
        Connectors = Array.Empty<object>(),
        Persistence = new { Provider = "sqlite" },
        Telemetry = new { Enabled = false },
        Model = new
        {
            ModelId = modelId,
            Provider = provider,
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
