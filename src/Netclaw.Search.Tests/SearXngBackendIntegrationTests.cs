// -----------------------------------------------------------------------
// <copyright file="SearXngBackendIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Netclaw.Search;
using Xunit;

namespace Netclaw.Search.Tests;

/// <summary>
/// Smoke test against a real SearXNG container; catches wire-format drift on image bump.
/// Self-skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public class SearXngBackendIntegrationTests : IAsyncLifetime
{
    // Pin a specific tag — never `latest`. Bump deliberately so wire-format drift
    // surfaces as an explicit code change, not a silent CI run.
    private const string SearXngImage = "searxng/searxng:2026.5.6-a9909c497";

    private IContainer? _container;
    private string? _endpoint;

    public async ValueTask InitializeAsync()
    {
        var settingsYml = LoadFixture("searxng-settings.yml");

        var container = new ContainerBuilder()
            .WithImage(SearXngImage)
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(settingsYml),
                "/etc/searxng/settings.yml")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
                r.ForPort(8080).ForPath("/healthz")))
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            await container.DisposeAsync();
            Assert.Skip($"Docker is not available; integration test skipped. ({ex.GetType().Name}: {ex.Message})");
            return;
        }

        _container = container;
        _endpoint = $"http://localhost:{container.GetMappedPublicPort(8080)}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    [Fact]
    public async Task Live_query_returns_parseable_json()
    {
        if (_endpoint is null)
        {
            Assert.Skip("Container did not start (Docker unavailable).");
            return;
        }

        var backend = new SearXngBackend(_endpoint);
        var result = await backend.SearchAsync("akka.net", 5, CancellationToken.None);

        // We assert on the wire-format contract, not result count: a sandboxed CI runner
        // may have limited or no upstream-engine connectivity, in which case SearXNG returns
        // an empty results array. That still proves JSON output works and our parser handled
        // the response shape. Failure here means SearXNG changed its JSON contract.
        Assert.IsType<SearchBackendResult.Success>(result);
    }

    /// <summary>
    /// Recognizes the exception types Testcontainers throws when no Docker daemon is reachable.
    /// On Windows CI runners and dev machines without Docker, StartAsync surfaces these as
    /// the underlying transport error ("Cannot connect to the Docker daemon", named pipe errors,
    /// or Docker.DotNet's connection-failure exceptions). On Linux runners with Docker, none
    /// of these match and the test runs normally.
    /// </summary>
    private static bool IsDockerUnavailable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var typeName = current.GetType().Name;
            if (typeName.Contains("Docker", StringComparison.Ordinal))
                return true;

            var msg = current.Message ?? "";
            if (msg.Contains("Docker", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("named pipe", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("/var/run/docker.sock", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string LoadFixture(string filename)
    {
        var assembly = typeof(SearXngBackendIntegrationTests).Assembly;
        var resourceName = $"Netclaw.Search.Tests.Fixtures.{filename}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Fixture not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
