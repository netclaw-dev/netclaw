// -----------------------------------------------------------------------
// <copyright file="ModelCatalogEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ModelCatalogEndpointTests
{
    [Fact]
    public async Task ModelsEndpoint_RequiresAuthorization()
    {
        using var dir = new DisposableTempDir();
        var probe = new RecordingProviderProbe();
        await using var host = await CreateHostAsync(dir.Path, CreateState(), probe);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, probe.ProbeCallCount);
    }

    [Fact]
    public async Task ModelsEndpoint_ReturnsLiveModelsFromConfiguredMainProvider()
    {
        using var dir = new DisposableTempDir();
        var state = CreateState();
        var probe = new RecordingProviderProbe
        {
            Result = new ProviderProbeResult(
                true,
                "using provider fallback",
                [
                    new DiscoveredModel
                    {
                        ModelId = new ModelId("provider-live-a"),
                        ContextWindowTokens = 128_000,
                        InputModalities = ModelModality.Text | ModelModality.Image,
                        OutputModalities = ModelModality.Text,
                    },
                    new DiscoveredModel
                    {
                        ModelId = new ModelId("provider-live-b"),
                    },
                ]),
        };
        await using var host = await CreateHostAsync(dir.Path, state, probe);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, TestAuthHandler.HeaderValue);

        var catalog = await client.GetFromJsonAsync<ModelCatalogWire.GetCatalogResponse>(
            "/api/models",
            TestContext.Current.CancellationToken);

        Assert.NotNull(catalog);
        Assert.Equal("using provider fallback", catalog.Warning);
        Assert.Equal(2, catalog.Models.Count);
        Assert.Same(state.Providers["configured-provider"], probe.LastEntry);

        var first = catalog.Models[0];
        Assert.Equal("configured-provider", first.Provider);
        Assert.Equal("provider-live-a", first.ModelId);
        Assert.Equal("provider-live-a", first.DisplayName);
        Assert.Equal(128_000, first.ContextWindow);
        Assert.Equal(["Text", "Image"], first.InputModalities);
        Assert.Equal(["Text"], first.OutputModalities);

        var second = catalog.Models[1];
        Assert.Equal("configured-provider", second.Provider);
        Assert.Equal("provider-live-b", second.ModelId);
        Assert.Empty(second.InputModalities);
        Assert.Empty(second.OutputModalities);
    }

    [Fact]
    public async Task ModelsEndpoint_ReturnsBadGatewayWhenProviderProbeFails()
    {
        using var dir = new DisposableTempDir();
        var probe = new RecordingProviderProbe
        {
            Result = new ProviderProbeResult(false, "API key is required.", []),
        };
        await using var host = await CreateHostAsync(dir.Path, CreateState(), probe);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, TestAuthHandler.HeaderValue);

        var response = await client.GetAsync("/api/models", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("configured-provider", body);
        Assert.Contains("API key is required.", body);
    }

    private static ConfiguredModelProviderState CreateState()
    {
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["configured-provider"] = new()
            {
                Type = "openrouter",
                Endpoint = "https://provider.example.test",
                AuthMethod = AuthMethod.ApiKey,
                ApiKey = new SensitiveString("sk-test"),
            },
        };
        var models = new ModelSelection
        {
            Main = new ModelReference
            {
                Provider = "configured-provider",
                ModelId = "provider-live-a",
            },
        };

        return new ConfiguredModelProviderState(providers, models);
    }

    private static async Task<WebApplication> CreateHostAsync(
        string homeDirectory,
        ConfiguredModelProviderState state,
        IProviderProbe probe)
    {
        var paths = new NetclawPaths(homeDirectory);
        paths.EnsureDirectoriesExist();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services
            .AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(probe);
        builder.Services.AddSingleton<ModelCatalogPersistence>();
        builder.Services.AddSingleton<ModelCatalogService>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapModelCatalogEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class RecordingProviderProbe : IProviderProbe
    {
        public ProviderProbeResult Result { get; set; } = new(true, null, []);
        public ProviderEntry? LastEntry { get; private set; }
        public int ProbeCallCount { get; private set; }

        public Task<ProviderProbeResult> ProbeAsync(
            string providerType,
            string? endpoint,
            string? apiKey,
            CancellationToken ct = default)
            => Task.FromResult(Result);

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        {
            ProbeCallCount++;
            LastEntry = entry;
            return Task.FromResult(Result);
        }

        public Task<ProviderProbeResult> ProbeAsync(
            string providerType,
            string? endpoint,
            string? credential,
            AuthMethod authMethod,
            CancellationToken ct = default)
            => Task.FromResult(Result);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestAuth";
        public const string HeaderName = "X-Test-Auth";
        public const string HeaderValue = "ok";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var value) || value != HeaderValue)
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user")], SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
