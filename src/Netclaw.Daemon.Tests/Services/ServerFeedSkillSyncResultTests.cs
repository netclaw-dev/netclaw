// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncResultTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Security.Skills;
using Netclaw.SkillClient;
using Netclaw.Tests.Utilities;
using Xunit;
using SkillScanResult = Netclaw.Security.Skills.SkillScanResult;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ServerFeedSkillSyncResultTests : IDisposable
{
    private const string BaseUrl = "https://skillserver.test/";
    private readonly DisposableTempDir _directory = new();
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _registry = new();

    public ServerFeedSkillSyncResultTests()
    {
        _paths = new NetclawPaths(_directory.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Rejected_skill_preserves_old_bytes_and_reports_partial_source_failure()
    {
        var oldContent = SkillMarkdown("blocked", "Old body.");
        var oldPath = Path.Join(_paths.ServerFeedDirectory("team"), "blocked", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllText(oldPath, oldContent);
        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedSyncStatePath("team"), new SkillSyncState
        {
            Skills =
            {
                ["blocked"] = new SyncedSkillState
                {
                    Version = "1.0.0",
                    Sha256 = SkillSyncHelpers.ComputeSha256(oldContent),
                },
            },
        });

        var handler = new FakeHttpMessageHandler();
        AddSkills(handler, ("blocked", "Rejected body."), ("healthy", "Accepted body."));
        var service = CreateService(handler, new RejectSelectedSkillScanner(), static (_, _) => true);
        var result = await RunAsync(service);

        var source = Assert.Single(result.Sources);
        Assert.Equal(1, source.ChangedCount);
        Assert.Equal(1, source.RejectedCount);
        Assert.Equal(0, source.FailedCount);
        Assert.Equal("absent", source.Sidecar);
        Assert.True(result.Inventory.Succeeded);
        Assert.Equal(oldContent, File.ReadAllText(oldPath));
        Assert.NotNull(_registry.GetByName("healthy"));
        var state = JsonSerializer.Deserialize<SkillSyncState>(
            File.ReadAllText(_paths.ServerFeedSyncStatePath("team")))!;
        Assert.Equal("1.0.0", state.Skills["blocked"].Version);
        Assert.Equal("2.0.0", state.Skills["healthy"].Version);
    }

    [Fact]
    public async Task Source_exception_returns_a_safe_failure_row_and_still_refreshes_inventory()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Private fixture error."));
        var service = CreateService(handler, new NoOpSkillContentScanner(), static (_, _) => true);

        var result = await RunAsync(service);

        var source = Assert.Single(result.Sources);
        Assert.Equal(1, source.FailedCount);
        Assert.Equal("not-run", source.Sidecar);
        Assert.Equal("The source sync failed. Existing files remain in use.", source.Error);
        Assert.DoesNotContain("Private fixture error", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.True(result.Inventory.Succeeded);
    }

    [Fact]
    public async Task Prune_counts_owned_orphans_and_missing_receipts_in_the_source_result()
    {
        var orphanDir = Path.Join(_paths.ServerFeedDirectory("team"), "orphan");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Join(orphanDir, "SKILL.md"), SkillMarkdown("orphan", "Old body."));
        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedSyncStatePath("team"), new SkillSyncState
        {
            Skills = { ["missing"] = new SyncedSkillState { Version = "1.0.0", Sha256 = "old" } },
        });

        var handler = new FakeHttpMessageHandler();
        AddSkills(handler, ("healthy", "Accepted body."));
        var service = CreateService(handler, new NoOpSkillContentScanner(), static (_, _) => true);
        var result = await RunAsync(service);

        var source = Assert.Single(result.Sources);
        Assert.Equal(3, source.ChangedCount);
        Assert.Equal(0, source.FailedCount);
        Assert.False(Directory.Exists(orphanDir));
        var state = JsonSerializer.Deserialize<SkillSyncState>(
            File.ReadAllText(_paths.ServerFeedSyncStatePath("team")))!;
        Assert.Equal(["healthy"], state.Skills.Keys.OrderBy(name => name));
        Assert.NotNull(_registry.GetByName("healthy"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Advertised_sidecar_page_failure_reports_failure_and_preserves_managed_agents(bool malformedPage)
    {
        var agentPath = Path.Join(_paths.ServerFeedAgentDirectory("team"), "reviewer.md");
        Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
        File.WriteAllText(agentPath, "Existing managed agent.");
        SkillSyncHelpers.WriteSyncState(_paths.ServerFeedAgentSyncStatePath("team"), new SkillSyncState
        {
            Skills = { ["reviewer"] = new SyncedSkillState { Version = "1.0.0", Sha256 = "old" } },
        });
        var stateBefore = File.ReadAllBytes(_paths.ServerFeedAgentSyncStatePath("team"));
        var handler = new FakeHttpMessageHandler();
        AddSkills(handler, ("healthy", "Accepted body."));
        handler.AddStringResponse(BaseUrl + "subagents/v1/index.json", """
            {"kind":"subagent-collection-index","pages":[{"range":"a-z","href":"/subagents/v1/pages/a-z.json"}]}
            """, "application/json");
        if (malformedPage)
            handler.AddStringResponse(BaseUrl + "subagents/v1/pages/a-z.json", "{broken", "application/json");
        else
            handler.AddErrorResponse(BaseUrl + "subagents/v1/pages/a-z.json", HttpStatusCode.NotFound);

        var service = CreateService(handler, new NoOpSkillContentScanner(), static (_, _) => true);
        var result = await RunAsync(service);

        var source = Assert.Single(result.Sources);
        Assert.Equal("failed", source.Sidecar);
        Assert.Equal(1, source.FailedCount);
        Assert.Equal(1, source.ChangedCount);
        Assert.Equal("Existing managed agent.", File.ReadAllText(agentPath));
        Assert.Equal(stateBefore, File.ReadAllBytes(_paths.ServerFeedAgentSyncStatePath("team")));
        Assert.NotNull(_registry.GetByName("healthy"));
    }

    [Fact]
    public async Task Inventory_publication_failure_overrides_successful_download_result()
    {
        var handler = new FakeHttpMessageHandler();
        AddSkills(handler, ("healthy", "Accepted body."));
        var publicationCalls = 0;
        var service = CreateService(handler, new NoOpSkillContentScanner(), (_, _) =>
        {
            Interlocked.Increment(ref publicationCalls);
            throw new InvalidOperationException("Fixture publication failure.");
        });

        var result = await RunAsync(service);

        Assert.True(publicationCalls > 0);
        Assert.Equal(0, Assert.Single(result.Sources).FailedCount);
        Assert.False(result.Inventory.Succeeded);
        Assert.Equal("The skill inventory refresh failed.", result.Inventory.Error);
        Assert.True(File.Exists(Path.Join(_paths.ServerFeedDirectory("team"), "healthy", "SKILL.md")));
    }

    private ServerFeedSkillSyncService CreateService(
        HttpMessageHandler handler,
        ISkillContentScanner scanner,
        Func<SkillEntry, TrustAudience, bool> visibility) => new(
        new SkillFeedsConfig
        {
            SyncIntervalMinutes = 0,
            Feeds = [new SkillFeedSource { Name = "team", Url = BaseUrl, TimeoutSeconds = 30 }],
        },
        _paths,
        _registry,
        new SkillIndexPublisher(_registry, new SkillIndexContextLayer(), visibility),
        TimeProvider.System,
        scanner,
        NullLogger<ServerFeedSkillSyncService>.Instance,
        [],
        feed => new SkillServerClient(new HttpClient(handler) { BaseAddress = new Uri(feed.Url) }));

    private static Task<SkillSyncResult.Response> RunAsync(ServerFeedSkillSyncService service)
        => service.SyncAsync(TestContext.Current.CancellationToken);

    private static void AddSkills(FakeHttpMessageHandler handler, params (string Name, string Body)[] skills)
    {
        handler.AddJsonResponse(BaseUrl + ".well-known/agent-skills/index.json", new
        {
            skills = skills.Select(skill => new
            {
                name = skill.Name,
                description = "Fixture guidance",
                url = BaseUrl + skill.Name + "/SKILL.md",
                version = "2.0.0",
                digest = "sha256:" + SkillSyncHelpers.ComputeSha256(SkillMarkdown(skill.Name, skill.Body)),
            }).ToArray(),
        });
        foreach (var skill in skills)
            handler.AddStringResponse(BaseUrl + skill.Name + "/SKILL.md", SkillMarkdown(skill.Name, skill.Body));
    }

    private static string SkillMarkdown(string name, string body)
        => $"---\nname: {name}\ndescription: Fixture guidance\n---\n\n{body}\n";

    private sealed class RejectSelectedSkillScanner : ISkillContentScanner
    {
        public Task<SkillScanResult> ScanAsync(string skillName, string content, CancellationToken cancellationToken)
            => Task.FromResult(skillName == "blocked"
                ? SkillScanResult.Reject("Fixture security rejection.")
                : SkillScanResult.Allow());
    }
}
