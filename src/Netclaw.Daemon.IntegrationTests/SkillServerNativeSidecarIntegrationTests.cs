// -----------------------------------------------------------------------
// <copyright file="SkillServerNativeSidecarIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Security.Skills;
using Netclaw.SkillClient;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.IntegrationTests;

/// <summary>
/// Opt-in spike against a real SkillServer container. This catches drift between
/// SkillServer's native sidecar wire format and NetClaw's sync adapter.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SkillServerNativeSidecarIntegrationTests : IAsyncLifetime
{
    private const string Image = "ghcr.io/netclaw-dev/skillserver:0.4.0-beta.3";
    private const string ApiKey = "sk-test-native-sidecar-sync";
    private const int ContainerPort = 8080;
    private const int HostPort = 18080;
    private const string ServerUrl = "http://localhost:18080";

    private IContainer? _container;
    private string? _skipReason;

    public async ValueTask InitializeAsync()
    {
        var optIn = Environment.GetEnvironmentVariable("NETCLAW_RUN_SKILLSERVER_INTEGRATION_TESTS");
        if (!string.Equals(optIn, "1", StringComparison.Ordinal))
        {
            _skipReason = "SkillServer integration test is opt-in; set NETCLAW_RUN_SKILLSERVER_INTEGRATION_TESTS=1 to run.";
            return;
        }

        IContainer? container = null;
        try
        {
            container = new ContainerBuilder(Image)
                .WithPortBinding(HostPort, ContainerPort)
                .WithEnvironment("SKILLSERVER__DATAPATH", "/tmp/skillserver-data")
                .WithEnvironment("SKILLSERVER__BASEURL", ServerUrl)
                .WithEnvironment("SKILLSERVER__APIKEY", ApiKey)
                .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
                    r.ForPort(ContainerPort).ForPath("/health")))
                .Build();

            await container.StartAsync();
        }
        catch (Exception ex) when (IsDockerOrPortUnavailable(ex))
        {
            if (container is not null)
                await container.DisposeAsync();

            _skipReason = $"Docker or fixed host port {HostPort} is unavailable; SkillServer integration test skipped. ({ex.GetType().Name}: {ex.Message})";
            return;
        }

        _container = container;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    [Fact]
    public async Task Syncs_skill_and_subagent_from_real_skillserver_container()
    {
        if (_skipReason is not null)
        {
            Assert.Skip(_skipReason);
            return;
        }

        await SeedSkillServerAsync();

        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        var feedsConfig = new SkillFeedsConfig
        {
            SyncIntervalMinutes = 0,
            Feeds =
            [
                new SkillFeedSource
                {
                    Name = "real-skillserver",
                    Url = ServerUrl,
                    ApiKey = new SensitiveString(ApiKey),
                    TimeoutSeconds = 30
                }
            ]
        };
        var skillRegistry = new SkillRegistry();
        var skillIndexLayer = new SkillIndexContextLayer();
        var service = new ServerFeedSkillSyncService(
            feedsConfig,
            paths,
            skillRegistry,
            new SkillIndexPublisher(skillRegistry, skillIndexLayer, static (_, _) => true),
            TimeProvider.System,
            new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            []);

        await service.SyncOnceAsync(CancellationToken.None);

        var skillPath = Path.Combine(paths.ServerFeedDirectory("real-skillserver"), "review-code", "SKILL.md");
        Assert.True(File.Exists(skillPath));
        Assert.Contains("metadata:", File.ReadAllText(skillPath), StringComparison.Ordinal);

        var agentPath = Path.Combine(
            paths.ServerFeedAgentDirectory("real-skillserver"),
            "code-reviewer.md");
        Assert.True(File.Exists(agentPath));
        Assert.Contains("You review code for concrete risks.", File.ReadAllText(agentPath), StringComparison.Ordinal);

        var loader = new FileSubAgentDefinitionLoader(
            paths,
            NullLogger<FileSubAgentDefinitionLoader>.Instance,
            feedsConfig);
        var registry = new SubAgentDefinitionRegistry();
        Assert.True(loader.SyncInto(registry));
        Assert.NotNull(registry.TryGetByName("code-reviewer"));
    }

    private static async Task SeedSkillServerAsync()
    {
        using var client = new SkillServerClient(ServerUrl, ApiKey);

        var skillContent = """
            ---
            name: review-code
            description: Route code review work to the managed reviewer sub-agent.
            metadata:
              subagent: code-reviewer
            ---

            # Review Code

            Use the `code-reviewer` sub-agent for bounded code review tasks.
            """;
        await using var skillStream = TextStream(skillContent);
        await client.UploadSkillIfNotExistsAsync(
            "review-code",
            "1.0.0",
            skillStream,
            [],
            category: null,
            CancellationToken.None);

        var subAgentContent = """
            ---
            name: code-reviewer
            description: Reviews code for concrete risks and regressions.
            tools: [file_read]
            visibility: user-facing
            ---

            You review code for concrete risks. Report findings first, with file and line references when available.
            """;
        await using var subAgentStream = TextStream(subAgentContent);
        await client.UploadSubAgentIfNotExistsAsync(
            "code-reviewer",
            "1.0.0",
            subAgentStream,
            CancellationToken.None);
    }

    private static MemoryStream TextStream(string content)
        => new(Encoding.UTF8.GetBytes(content));

    private static bool IsDockerOrPortUnavailable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name.Contains("Docker", StringComparison.Ordinal))
                return true;

            var msg = current.Message ?? "";
            if (msg.Contains("Docker", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("named pipe", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("/var/run/docker.sock", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)
                || msg.Contains($":{HostPort}", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
