// -----------------------------------------------------------------------
// <copyright file="SkillInventoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SkillInventoryTests
{
    [Fact]
    public void Server_feed_list_and_detail_preserve_the_managed_source_and_version()
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "skill-inventory-projection"));
        var directory = Path.Combine(paths.ServerFeedDirectory("team"), "git");
        var file = Path.Combine(directory, "SKILL.md");
        var entry = new SkillEntry("git", "Git Hygiene", "Git hygiene.",
            new FileSkillSource(file, directory), ".server-feeds") { Version = "1.3.1" };

        var row = Assert.Single(SkillInventory.From([entry], paths).Skills);
        var detail = SkillInventory.DetailView(entry, paths, "# Git Hygiene");

        Assert.Equal("server-feed", row.Source);
        Assert.Equal(row.Source, detail.Skill.Source);
        Assert.Equal("1.3.1", detail.Skill.Version);
        Assert.Equal(file, detail.FilePath);
        Assert.Equal(directory, detail.SkillDirectory);
        Assert.Equal("# Git Hygiene", detail.Content);
    }
}
