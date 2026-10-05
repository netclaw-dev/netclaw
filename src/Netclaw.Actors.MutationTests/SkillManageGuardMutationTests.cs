// -----------------------------------------------------------------------
// <copyright file="SkillManageGuardMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Focused tests for <c>SkillManageTool.GuardMutationTarget</c>. Each denial
/// test also proves that the file outside the skill does not change.
/// </summary>
public sealed class SkillManageGuardMutationTests : IDisposable
{
    private const string LinkDenied = "Symlink traversal is not allowed in skill file paths.";
    private const string ProtectedDenied = "The target path is protected.";

    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(),
        "netclaw-mutation-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _outside;
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _registry = new();

    public SkillManageGuardMutationTests()
    {
        _paths = new NetclawPaths(_basePath);
        _paths.EnsureDirectoriesExist();
        _outside = Path.Combine(_basePath, "outside");
        Directory.CreateDirectory(_outside);
        WriteSkill(Path.Combine(_paths.SkillsDirectory, "guarded", "SKILL.md"), "guarded");
        WriteSkill(Path.Combine(_paths.SkillsDirectory, "flat.md"), "flat");
        var scan = SkillScanner.Scan(_paths.SkillsDirectory);
        _registry.ReplaceAll(scan.AcceptedSkills, scan.Issues);
    }

    [Fact]
    public async Task Write_inside_skill_directory_succeeds()
    {
        var result = await WriteFileAsync("guarded", "references/notes.md");

        Assert.StartsWith("File written: references/notes.md", result);
        Assert.True(File.Exists(Path.Combine(_paths.SkillsDirectory, "guarded", "references", "notes.md")));
    }

    [Fact]
    public async Task Write_through_linked_directory_is_denied()
    {
        Directory.CreateSymbolicLink(Path.Combine(_paths.SkillsDirectory, "guarded", "references"), _outside);

        var result = await WriteFileAsync("guarded", "references/new.txt");

        Assert.StartsWith(LinkDenied, result);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    [Fact]
    public async Task Write_through_link_at_atomic_temp_path_is_denied()
    {
        var outsideFile = Path.Combine(_outside, "config.json");
        File.WriteAllText(outsideFile, "original");
        var references = Path.Combine(_paths.SkillsDirectory, "guarded", "references");
        Directory.CreateDirectory(references);
        File.CreateSymbolicLink(Path.Combine(references, "guide.md.tmp"), outsideFile);

        var result = await WriteFileAsync("guarded", "references/guide.md");

        Assert.StartsWith(LinkDenied, result);
        Assert.Equal("original", File.ReadAllText(outsideFile));
    }

    // No production protected path is below the skills root (owner decision,
    // 2026-10-05: the skill tiers are not control plane). A policy that protects
    // one folder of the skill proves that the guard still keeps the protection.
    [Fact]
    public async Task Write_into_a_protected_path_is_denied()
    {
        var locked = Path.Combine(_paths.SkillsDirectory, "guarded", "locked");

        var result = await WriteFileAsync("guarded", "locked/notes.md", new ToolPathPolicy([locked]));

        Assert.StartsWith(ProtectedDenied, result);
        Assert.False(File.Exists(Path.Combine(locked, "notes.md")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    private Task<string> WriteFileAsync(string skill, string filePath)
        => WriteFileAsync(skill, filePath, DaemonToolPathPolicyFactory.Create(_paths, ShellExecutionEnvironmentDefaults.Bash));

    private Task<string> WriteFileAsync(string skill, string filePath, ToolPathPolicy protectedPaths)
    {
        var refresher = new SkillInventoryRefresher(
            _paths,
            new SkillFeedsConfig(),
            [],
            _registry,
            new SkillIndexPublisher(_registry, new SkillIndexContextLayer(), static (_, _) => true));
        var tool = new SkillManageTool(
            _registry,
            _paths,
            new NoOpSkillContentScanner(),
            refresher,
            protectedPaths);
        var arguments = new Dictionary<string, object?>
        {
            ["Action"] = "write_file",
            ["Name"] = skill,
            ["FilePath"] = filePath,
            ["FileContent"] = "content"
        };
        return tool.ExecuteAsync(arguments, CreateContext(), CancellationToken.None);
    }

    private static void WriteSkill(string path, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"---\nname: {name}\ndescription: Guard test.\n---\n# Guard\n");
    }

    private ToolInvocationContext CreateContext() =>
        new(
            new ToolRunScope
            {
                Session = new ToolSessionScope.Bound(
                    "signalr/current",
                    SessionStoragePaths.CreateVersion2(
                        new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "current")))),
                Audience = TrustAudience.Personal,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Personal),
                InlineOutputBudget = InlineOutputBudget.Default,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
            },
            ToolExecutionTimeout.Default);
}
