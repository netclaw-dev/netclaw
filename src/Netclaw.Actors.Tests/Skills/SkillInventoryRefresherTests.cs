// -----------------------------------------------------------------------
// <copyright file="SkillInventoryRefresherTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Skills;

public sealed class SkillInventoryRefresherTests : IDisposable
{
    private readonly string _home = Path.Join(
        Path.GetTempPath(), $"netclaw-inventory-test-{Guid.NewGuid():N}");
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _registry = new();
    private readonly SkillIndexContextLayer _index = new();

    public SkillInventoryRefresherTests()
    {
        _paths = new NetclawPaths(_home);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public void Refresh_discovers_server_feed_directory_created_after_construction()
    {
        var feeds = new SkillFeedsConfig
        {
            Feeds = [new SkillFeedSource { Name = "managed" }]
        };
        var refresher = new SkillInventoryRefresher(_paths, feeds, [], _registry, _index);

        Assert.Empty(refresher.Refresh().AcceptedSkills);

        WriteSkill(_paths.ServerFeedDirectory("managed"), "feed-skill", "managed guidance");
        var result = refresher.Refresh();

        Assert.Contains(result.AcceptedSkills, skill => skill.Name == "feed-skill");
        Assert.Contains("feed-skill: managed guidance", _index.GetContextLayer(TrustAudience.Personal));
    }

    [Fact]
    public void Refresh_preserves_all_sources_and_applies_canonical_precedence()
    {
        var feedRoot = _paths.ServerFeedDirectory("managed");
        var externalRoot = Path.Join(_home, "external");
        WriteSkill(_paths.SkillsDirectory, "shared", "native wins");
        WriteSkill(feedRoot, "shared", "feed loses");
        WriteSkill(feedRoot, "feed-only", "managed");
        WriteSkill(externalRoot, "external-only", "external");

        var feeds = new SkillFeedsConfig
        {
            Feeds = [new SkillFeedSource { Name = "managed" }]
        };
        var external = new[]
        {
            new ResolvedExternalSource("external", [externalRoot], AllowSymlinks: false)
        };
        var refresher = new SkillInventoryRefresher(_paths, feeds, external, _registry, _index);

        refresher.Refresh();
        WriteSkill(_paths.SkillsDirectory, "new-native", "created by mutation");
        var result = refresher.Refresh();

        Assert.Equal("native wins", _registry.GetByName("shared")!.Description);
        Assert.Contains(result.AcceptedSkills, skill => skill.Name == "feed-only");
        Assert.Contains(result.AcceptedSkills, skill => skill.Name == "external-only");
        Assert.Contains(result.AcceptedSkills, skill => skill.Name == "new-native");
    }

    [Fact]
    public void ReplaceAll_never_exposes_a_partially_replaced_inventory()
    {
        var a = new[] { Entry("a-1"), Entry("a-2") };
        var b = new[] { Entry("b-1"), Entry("b-2") };
        _registry.ReplaceAll(a);
        var failures = new ConcurrentQueue<string>();

        Parallel.For(0, 10_000, iteration =>
        {
            if ((iteration & 1) == 0)
                _registry.ReplaceAll((iteration & 2) == 0 ? a : b);
            else
            {
                var snapshot = _registry.GetAll();
                if (snapshot.Count != 2 || snapshot.Any(skill => skill.Name[0] != snapshot[0].Name[0]))
                    failures.Enqueue(string.Join(',', snapshot.Select(skill => skill.Name)));
            }
        });

        Assert.Empty(failures);
    }

    private static SkillEntry Entry(string name) => new(
        name,
        name,
        "description",
        $"/skills/{name}/SKILL.md",
        $"/skills/{name}",
        Category: null);

    private static void WriteSkill(string root, string name, string description)
    {
        var directory = Path.Join(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, "SKILL.md"), $$"""
            ---
            name: {{name}}
            description: {{description}}
            ---
            # {{name}}
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }
}
