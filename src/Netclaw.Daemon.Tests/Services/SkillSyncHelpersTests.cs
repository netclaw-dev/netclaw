// -----------------------------------------------------------------------
// <copyright file="SkillSyncHelpersTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class SkillSyncHelpersTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string CreateSkillDir(string name)
    {
        var dir = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\n---\n");
        return dir;
    }

    private string CreateNonSkillDir(string name)
    {
        var dir = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SyncedSkillState State(string version = "1.0.0", string sha = "abc")
        => new() { Version = version, Sha256 = sha };

    [Fact]
    public void PruneRemovedSkills_drops_stale_dirs_and_state_but_keeps_present_and_bookkeeping()
    {
        var feedDir = _dir.Path;
        CreateSkillDir("skill-a");
        CreateSkillDir("skill-b");
        CreateSkillDir("skill-c");

        // Sync-service bookkeeping (dot-prefixed) that must never be pruned.
        Directory.CreateDirectory(Path.Combine(feedDir, ".staging"));
        File.WriteAllText(Path.Combine(feedDir, ".sync-state.json"), "{}");

        var syncState = new SkillSyncState();
        foreach (var name in new[] { "skill-a", "skill-b", "skill-c", "skill-d" })
            syncState.Skills[name] = new SyncedSkillState { Version = "1.0.0", Sha256 = "abc" };

        // Server index now advertises only skill-a.
        var changed = SkillSyncHelpers.PruneRemovedSkills(
            feedDir, new[] { "skill-a" }, syncState, NullLogger.Instance);

        Assert.True(changed);

        // On disk: skill-a kept, removed skills gone.
        Assert.True(Directory.Exists(Path.Combine(feedDir, "skill-a")));
        Assert.False(Directory.Exists(Path.Combine(feedDir, "skill-b")));
        Assert.False(Directory.Exists(Path.Combine(feedDir, "skill-c")));

        // Bookkeeping preserved.
        Assert.True(Directory.Exists(Path.Combine(feedDir, ".staging")));
        Assert.True(File.Exists(Path.Combine(feedDir, ".sync-state.json")));

        // State: only skill-a survives (skill-d had no dir but is dropped from state too).
        Assert.Equal(new[] { "skill-a" }, syncState.Skills.Keys.OrderBy(k => k));
    }

    [Fact]
    public void PruneRemovedSkills_is_a_no_op_when_index_covers_everything()
    {
        var feedDir = _dir.Path;
        CreateSkillDir("skill-a");
        CreateSkillDir("skill-b");

        var syncState = new SkillSyncState
        {
            Skills =
            {
                ["skill-a"] = new SyncedSkillState { Version = "1", Sha256 = "x" },
                ["skill-b"] = new SyncedSkillState { Version = "1", Sha256 = "y" },
            },
        };

        var changed = SkillSyncHelpers.PruneRemovedSkills(
            feedDir, new[] { "skill-a", "skill-b" }, syncState, NullLogger.Instance);

        Assert.False(changed);
        Assert.True(Directory.Exists(Path.Combine(feedDir, "skill-a")));
        Assert.True(Directory.Exists(Path.Combine(feedDir, "skill-b")));
        Assert.Equal(2, syncState.Skills.Count);
    }

    [Fact]
    public void PruneRemovedSkills_only_deletes_real_skill_directories()
    {
        var feedDir = _dir.Path;
        CreateSkillDir("skill-a");        // live skill, advertised
        CreateNonSkillDir("not-a-skill"); // no SKILL.md, not in state — must survive
        CreateNonSkillDir("husk");        // no SKILL.md, tracked in state — dir survives, state pruned

        var syncState = new SkillSyncState
        {
            Skills =
            {
                ["skill-a"] = State(),
                ["husk"] = State(),
            },
        };

        // Server advertises only skill-a.
        var changed = SkillSyncHelpers.PruneRemovedSkills(
            feedDir, new[] { "skill-a" }, syncState, NullLogger.Instance);

        Assert.True(changed); // husk's stale state entry was removed

        // A directory without a SKILL.md is never recursively deleted.
        Assert.True(Directory.Exists(Path.Combine(feedDir, "not-a-skill")));
        Assert.True(Directory.Exists(Path.Combine(feedDir, "husk")));
        Assert.True(Directory.Exists(Path.Combine(feedDir, "skill-a")));

        // State is pruned to what the server still advertises.
        Assert.Equal(new[] { "skill-a" }, syncState.Skills.Keys.OrderBy(k => k));
    }

    [Fact]
    public void PruneRemovedSkills_is_a_no_op_for_an_empty_index()
    {
        var feedDir = _dir.Path;
        CreateSkillDir("skill-a");

        var syncState = new SkillSyncState { Skills = { ["skill-a"] = State() } };

        // An empty server index is not authoritative — prune nothing.
        var changed = SkillSyncHelpers.PruneRemovedSkills(
            feedDir, Array.Empty<string>(), syncState, NullLogger.Instance);

        Assert.False(changed);
        Assert.True(Directory.Exists(Path.Combine(feedDir, "skill-a")));
        Assert.Single(syncState.Skills);
    }

    [Fact]
    public void PruneRemovedSubAgents_drops_stale_managed_files_and_state_only()
    {
        var feedDir = _dir.Path;
        File.WriteAllText(Path.Combine(feedDir, "agent-a.md"), "a");
        File.WriteAllText(Path.Combine(feedDir, "agent-b.md"), "b");
        File.WriteAllText(Path.Combine(feedDir, "notes.txt"), "not managed");
        Directory.CreateDirectory(Path.Combine(feedDir, ".staging"));

        var syncState = new SkillSyncState
        {
            Skills =
            {
                ["agent-a"] = State(),
                ["agent-b"] = State(),
                ["agent-c"] = State(),
            }
        };

        var changed = SkillSyncHelpers.PruneRemovedSubAgents(
            feedDir, ["agent-a"], syncState, NullLogger.Instance);

        Assert.True(changed);
        Assert.True(File.Exists(Path.Combine(feedDir, "agent-a.md")));
        Assert.False(File.Exists(Path.Combine(feedDir, "agent-b.md")));
        Assert.True(File.Exists(Path.Combine(feedDir, "notes.txt")));
        Assert.True(Directory.Exists(Path.Combine(feedDir, ".staging")));
        Assert.Equal(["agent-a"], syncState.Skills.Keys.OrderBy(k => k));
    }

    [Theory]
    [InlineData("code-reviewer", true)]
    [InlineData("agent123", true)]
    [InlineData("CodeReviewer", false)]
    [InlineData("../agent", false)]
    [InlineData("agent.md", false)]
    [InlineData("", false)]
    public void IsSafeManagedFileStem_accepts_only_lowercase_alphanumeric_hyphen(
        string value,
        bool expected)
    {
        Assert.Equal(expected, SkillSyncHelpers.IsSafeManagedFileStem(value));
    }
}
