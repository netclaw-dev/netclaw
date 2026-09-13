// -----------------------------------------------------------------------
// <copyright file="BuiltInSkillSeedingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests;

public sealed class BuiltInSkillSeedingTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Restore_writes_the_complete_embedded_tree()
    {
        var paths = CreatePaths();

        EmbeddedSystemSkillRestorer.Restore(paths);

        var sourceDirectory = FindSourceDirectory();
        var sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var embeddedFiles = typeof(EmbeddedSystemSkillRestorer).Assembly
            .GetManifestResourceNames()
            .Where(EmbeddedSystemSkillRestorer.IsSystemSkillResource)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var restoredFiles = Directory.EnumerateFiles(paths.SystemSkillsDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(paths.SystemSkillsDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(embeddedFiles);
        Assert.Equal(sourceFiles, restoredFiles);
        Assert.Equal(
            embeddedFiles.Select(EmbeddedSystemSkillRestorer.GetResourceRelativePath),
            restoredFiles);

        foreach (var relativePath in sourceFiles)
        {
            var targetPath = Path.Combine(paths.SystemSkillsDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var sourcePath = Path.Combine(sourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath));

            if (!OperatingSystem.IsWindows())
            {
                var executableBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                Assert.Equal(
                    File.GetUnixFileMode(sourcePath) & executableBits,
                    File.GetUnixFileMode(targetPath) & executableBits);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            var sourceExecutablePaths = new List<string>();
            foreach (var sourceFile in sourceFiles)
            {
                var sourcePath = Path.Combine(sourceDirectory, sourceFile.Replace('/', Path.DirectorySeparatorChar));
                if (File.GetUnixFileMode(sourcePath).HasFlag(UnixFileMode.UserExecute))
                    sourceExecutablePaths.Add(sourceFile);
            }
            using var manifestStream = typeof(EmbeddedSystemSkillRestorer).Assembly
                .GetManifestResourceStream("Netclaw.SystemSkillExecutablePaths");
            Assert.NotNull(manifestStream);
            var manifestExecutablePaths = JsonSerializer.Deserialize<string[]>(manifestStream)!;

            Assert.Equal(sourceExecutablePaths.Order(StringComparer.Ordinal), manifestExecutablePaths.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Restore_replaces_the_managed_tree_and_preserves_user_skills()
    {
        var paths = CreatePaths();
        var managedSkill = Path.Combine(paths.SystemSkillsDirectory, "netclaw-memory", "SKILL.md");
        var staleFile = Path.Combine(paths.SystemSkillsDirectory, "stale", "reference.md");
        var userSkill = Path.Combine(paths.SkillsDirectory, "user-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(managedSkill)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staleFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(userSkill)!);
        File.WriteAllText(managedSkill, "modified system content");
        File.WriteAllText(staleFile, "stale system content");
        File.WriteAllText(userSkill, "user content");

        EmbeddedSystemSkillRestorer.Restore(paths);

        Assert.DoesNotContain("modified system content", File.ReadAllText(managedSkill), StringComparison.Ordinal);
        Assert.False(File.Exists(staleFile));
        Assert.Equal("user content", File.ReadAllText(userSkill));
    }

    [Fact]
    public void Restore_removes_only_abandoned_swap_directories()
    {
        var paths = CreatePaths();
        var abandonedStaging = Path.Combine(paths.SkillsDirectory, $".system.staging-{Guid.NewGuid():N}");
        var abandonedBackup = Path.Combine(paths.SkillsDirectory, $".system.backup-{Guid.NewGuid():N}");
        var similarDirectory = Path.Combine(paths.SkillsDirectory, ".system.backup-operator-data");
        Directory.CreateDirectory(abandonedStaging);
        Directory.CreateDirectory(abandonedBackup);
        Directory.CreateDirectory(similarDirectory);
        File.WriteAllText(Path.Combine(abandonedStaging, "partial.md"), "partial staging content");
        File.WriteAllText(Path.Combine(abandonedBackup, "SKILL.md"), "old backup content");
        File.WriteAllText(Path.Combine(similarDirectory, "operator.md"), "operator content");

        EmbeddedSystemSkillRestorer.Restore(paths);

        Assert.False(Directory.Exists(abandonedStaging));
        Assert.False(Directory.Exists(abandonedBackup));
        Assert.True(Directory.Exists(similarDirectory));
    }

    [Fact(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsPosix),
        Skip = "Symbolic link fixture requires POSIX filesystem support")]
    public void Restore_rejects_an_abandoned_swap_symbolic_link()
    {
        var paths = CreatePaths();
        var externalDirectory = Path.Combine(_directory.Path, "external-swap-target");
        var sentinel = Path.Combine(externalDirectory, "sentinel.md");
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(sentinel, "must remain outside the managed tree");
        var abandonedLink = Path.Combine(paths.SkillsDirectory, $".system.staging-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(abandonedLink, externalDirectory);

        var exception = Assert.Throws<InvalidOperationException>(() => EmbeddedSystemSkillRestorer.Restore(paths));

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(abandonedLink));
        Assert.Equal("must remain outside the managed tree", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Restore_populates_the_registry_search_index()
    {
        var paths = CreatePaths();
        EmbeddedSystemSkillRestorer.Restore(paths);
        var registry = new SkillRegistry();
        var indexLayer = new SkillIndexContextLayer();
        var refresher = new SkillInventoryRefresher(
            paths,
            new SkillFeedsConfig(),
            [],
            registry,
            new SkillIndexPublisher(registry, indexLayer, static (_, _) => true));

        refresher.Refresh();

        Assert.Contains(registry.Search("diagnostics"), skill => skill.Name == "netclaw-operations");
    }

    [Fact(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsPosix),
        Skip = "Symbolic link fixture requires POSIX filesystem support")]
    public void Restore_rejects_a_symbolic_link_for_the_managed_tree()
    {
        var paths = CreatePaths();
        var externalDirectory = Path.Combine(_directory.Path, "external-system-skills");
        Directory.CreateDirectory(externalDirectory);
        var sentinel = Path.Combine(externalDirectory, "sentinel.md");
        File.WriteAllText(sentinel, "must remain outside the managed tree");
        Directory.Delete(paths.SystemSkillsDirectory);
        Directory.CreateSymbolicLink(paths.SystemSkillsDirectory, externalDirectory);

        var exception = Assert.Throws<InvalidOperationException>(() => EmbeddedSystemSkillRestorer.Restore(paths));

        Assert.Contains("symbolic link", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("must remain outside the managed tree", File.ReadAllText(sentinel));
    }

    [Fact(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsWindows),
        Skip = "This case uses native Windows junction semantics.")]
    public async Task Restore_rejects_a_windows_junction_for_the_managed_tree()
    {
        var paths = CreatePaths();
        var externalDirectory = Path.Combine(_directory.Path, "external-system-skills");
        var sentinel = Path.Combine(externalDirectory, "sentinel.md");
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(sentinel, "must remain outside the managed tree");
        Directory.Delete(paths.SystemSkillsDirectory);
        await CreateWindowsJunctionAsync(paths.SystemSkillsDirectory, externalDirectory);

        var exception = Assert.Throws<InvalidOperationException>(() => EmbeddedSystemSkillRestorer.Restore(paths));

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("must remain outside the managed tree", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Restore_keeps_an_unmanaged_file_when_the_tree_swap_fails()
    {
        var paths = CreatePaths();
        var abandonedBackup = Path.Combine(paths.SkillsDirectory, $".system.backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(abandonedBackup);
        Directory.Delete(paths.SystemSkillsDirectory);
        File.WriteAllText(paths.SystemSkillsDirectory, "unmanaged file");

        var exception = Assert.Throws<InvalidOperationException>(() => EmbeddedSystemSkillRestorer.Restore(paths));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains(paths.SystemSkillsDirectory, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Confirm that", exception.Message, StringComparison.Ordinal);
        Assert.Equal("unmanaged file", File.ReadAllText(paths.SystemSkillsDirectory));
        Assert.True(Directory.Exists(abandonedBackup));
    }

    [Fact(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsLinux),
        Skip = "Cleanup failure requires Linux directory permission semantics")]
    [SupportedOSPlatform("linux")]
    public void Restore_keeps_the_committed_tree_when_backup_cleanup_fails()
    {
        var paths = CreatePaths();
        var oldSkill = Path.Combine(paths.SystemSkillsDirectory, "old-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(oldSkill)!);
        File.WriteAllText(oldSkill, "old system content");
        File.SetUnixFileMode(paths.SystemSkillsDirectory, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var exception = Record.Exception(() => EmbeddedSystemSkillRestorer.Restore(paths));

            var restoreException = Assert.IsType<InvalidOperationException>(exception);
            Assert.IsType<UnauthorizedAccessException>(restoreException.InnerException);
            Assert.True(File.Exists(Path.Combine(paths.SystemSkillsDirectory, "netclaw-memory", "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(paths.SystemSkillsDirectory, "old-skill", "SKILL.md")));
        }
        finally
        {
            foreach (var backupDirectory in Directory.GetDirectories(paths.SkillsDirectory, ".system.backup-*"))
            {
                File.SetUnixFileMode(backupDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(backupDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Operations_skill_and_project_reference_share_tool_and_directory_order()
    {
        var paths = CreatePaths();
        EmbeddedSystemSkillRestorer.Restore(paths);
        var skillDirectory = Path.Combine(paths.SystemSkillsDirectory, "netclaw-operations");
        var skill = File.ReadAllText(Path.Combine(skillDirectory, "SKILL.md"));
        var projects = File.ReadAllText(Path.Combine(skillDirectory, "references", "projects.md"));

        Assert.Contains("use `file_read` for a known local file read", skill, StringComparison.Ordinal);
        Assert.Contains("use `web_search` for external discovery", skill, StringComparison.Ordinal);
        Assert.Contains("use `shell_execute` for local search", skill, StringComparison.Ordinal);
        Assert.Contains("Do not delegate a known file operation", skill, StringComparison.Ordinal);
        Assert.Contains("do not use shell only to verify", skill, StringComparison.Ordinal);
        Assert.Contains("do not attempt a shell redirect first", skill, StringComparison.Ordinal);
        Assert.Contains("Start with the smallest single shell operation", skill, StringComparison.Ordinal);
        Assert.Contains("Use one operation per call", skill, StringComparison.Ordinal);
        Assert.Contains("Keep independent searches and diagnostics separate", skill, StringComparison.Ordinal);
        Assert.Contains("do not join them with separators or labels", skill, StringComparison.Ordinal);
        Assert.Contains("Add a pipeline only when the requested result requires it", skill, StringComparison.Ordinal);
        Assert.Contains("If approval is required but no interactive requester is available", skill, StringComparison.Ordinal);
        Assert.Contains("After an access denial, do not retry that call during the same user turn", skill, StringComparison.Ordinal);
        Assert.Contains("A later explicit user request can start a new call", skill, StringComparison.Ordinal);
        Assert.Contains("Use `temp_dir` for disposable files", skill, StringComparison.Ordinal);
        Assert.Contains("Standard temporary APIs already use this directory", skill, StringComparison.Ordinal);
        Assert.Contains("Do not probe a named project path before declaring it", skill, StringComparison.Ordinal);
        Assert.Contains("user-provided fallback before other tools", skill, StringComparison.Ordinal);
        Assert.Contains("Use the task's first project path exactly", skill, StringComparison.Ordinal);
        Assert.Contains("Use `load_tool` directly for a known exact tool name", skill, StringComparison.Ordinal);
        Assert.Contains("Use `search_tools` when the capability is known", skill, StringComparison.Ordinal);

        var statements = new[]
        {
            "For declared-project work, omit `WorkingDirectory`",
            "For one call in a named child directory",
            "Use `temp_dir` for disposable files",
            "Use an inline directory change only when",
            "Start with the smallest single shell operation",
            "Use one operation per call",
            "Keep independent searches and diagnostics separate",
            "do not join them with separators or labels",
            "Add a pipeline only when the requested result requires it",
            "If approval is required but no interactive requester is available",
            "After an access denial, do not retry that call during the same user turn",
            "A later explicit user request can start a new call",
            "Apply all compatible advice in a correction response before the next call",
            "A shell call can return correction advice under Auto",
            "Advice grants no authority. Every replacement call passes current policy",
            "If you require the exact platform path, retry unchanged once through normal policy",
            "Reviewed diagnostics without file output do not receive temporary relocation advice"
        };
        foreach (var statement in statements)
        {
            Assert.Contains(statement, skill, StringComparison.Ordinal);
            Assert.Contains(statement, projects, StringComparison.Ordinal);
        }
    }

    private NetclawPaths CreatePaths()
    {
        var paths = new NetclawPaths(Path.Combine(_directory.Path, Guid.NewGuid().ToString("N")));
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static async Task CreateWindowsJunctionAsync(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var standardOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"mklink failed: {standardOutput}{standardError}");
    }

    private static string FindSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "feeds", "skills", ".system", "files");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("The system skill source tree is unavailable to the test.");
    }
}
