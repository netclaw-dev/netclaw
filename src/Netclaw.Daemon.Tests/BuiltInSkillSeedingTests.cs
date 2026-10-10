// -----------------------------------------------------------------------
// <copyright file="BuiltInSkillSeedingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests;

public sealed class BuiltInSkillSeedingTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    public void Dispose()
    {
        WindowsJunction.RemoveJunctionsUnder(_directory.Path);
        _directory.Dispose();
    }

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

    [Fact]
    public async Task Coordination_bundle_loads_inline_and_reads_all_resources_through_logical_tools()
    {
        var paths = CreatePaths();
        EmbeddedSystemSkillRestorer.Restore(paths);
        var registry = new SkillRegistry();
        var refresher = new SkillInventoryRefresher(paths, new SkillFeedsConfig(), [], registry,
            new SkillIndexPublisher(registry, new SkillIndexContextLayer(), static (_, _) => true));
        refresher.Refresh();

        var skill = registry.GetByName("agent-coordination");
        Assert.NotNull(skill);
        Assert.False(skill.HasSubagentRoutingMetadata);
        Assert.False(skill.DisableModelInvocation);
        Assert.Equal("1.0.4", skill.Version);
        Assert.Contains(registry.Search("code"), entry => entry.Name == skill.Name);
        string[] resources = [
            "assets/findings.md", "assets/plan.md", "references/analyze-plan.md",
            "references/diagnose-fix-verify.md", "references/implement-review.md", "references/parallel-research.md"
        ];
        Assert.Equal(resources.Order(StringComparer.Ordinal), skill.ResourcePaths!.Order(StringComparer.Ordinal));
        var context = TestToolExecutionContext.CreateUnboundWithoutApproval(TrustAudience.Personal);
        var load = new SkillLoadTool(registry, new NoOpSkillContentScanner(), new RejectUnexpectedPromptLoad());
        var receipt = await load.ExecuteAsync(ToolInput.Create("Name", skill.Name), context,
            TestContext.Current.CancellationToken);
        Assert.Contains("Assign one writer", receipt);
        Assert.Contains("A cancellation acceptance does not prove", receipt);
        Assert.Contains("Only the parent sends user messages", receipt);
        Assert.DoesNotContain(paths.SystemSkillsDirectory, receipt);
        Assert.True(receipt.Length < new SessionTuning().MaxInlineToolResultChars);

        var read = new SkillReadResourceTool(registry, new NoOpSkillContentScanner());
        foreach (var resource in resources)
        {
            Assert.Contains(resource, receipt);
            var result = await read.ExecuteAsync(
                ToolInput.Create("SkillName", skill.Name, "ResourcePath", resource), context,
                TestContext.Current.CancellationToken);
            var resourcePath = Path.Combine(skill.SkillDirectory, resource.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal($"path: {resourcePath}\n{File.ReadAllText(resourcePath)}", result);
            Assert.True(result.Length < new SessionTuning().MaxInlineToolResultChars);
        }

        var deniedContext = TestToolExecutionContext.CreateUnbound();
        Assert.Equal("Error: This tool is not available.", await load.ExecuteAsync(
            ToolInput.Create("Name", skill.Name), deniedContext, TestContext.Current.CancellationToken));
        Assert.Equal("Error: This tool is not available.", await read.ExecuteAsync(
            ToolInput.Create("SkillName", skill.Name, "ResourcePath", resources[0]), deniedContext,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Coordination_bundle_dispatch_preserves_full_content_inline_and_through_session_continuations()
    {
        var paths = CreatePaths();
        EmbeddedSystemSkillRestorer.Restore(paths);
        var skills = new SkillRegistry();
        var refresher = new SkillInventoryRefresher(paths, new SkillFeedsConfig(), [], skills,
            new SkillIndexPublisher(skills, new SkillIndexContextLayer(), static (_, _) => true));
        refresher.Refresh();
        var skill = skills.GetByName("agent-coordination");
        Assert.NotNull(skill);

        var tools = new ToolRegistry();
        tools.RegisterCore(new SkillLoadTool(skills, new NoOpSkillContentScanner(), new RejectUnexpectedPromptLoad()));
        tools.RegisterCore(new SkillReadResourceTool(skills, new NoOpSkillContentScanner()));
        tools.RegisterCore(new ToolOutputReadTool());
        var config = new ToolConfig();
        config.AudienceProfiles.Public.AllowedTools = ["skill_read_resource"];
        var executor = new DispatchingToolExecutor(tools, new ToolAccessPolicy(paths, config,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([])));
        var sessionDirectory = Path.Combine(_directory.Path, "coordination-session");
        Directory.CreateDirectory(sessionDirectory);
        var foreignDirectory = Path.Combine(_directory.Path, "foreign-session");
        Directory.CreateDirectory(foreignDirectory);
        var defaultBudget = new SessionTuning().MaxInlineToolResultChars;
        const int smallBudget = 512;
        const int pageLimit = 256;
        string[] resources = [
            "assets/findings.md", "assets/plan.md", "references/analyze-plan.md",
            "references/diagnose-fix-verify.md", "references/implement-review.md", "references/parallel-research.md"
        ];

        for (var index = 0; index <= resources.Length; index++)
        {
            var toolName = index == 0 ? "skill_load" : "skill_read_resource";
            var arguments = index == 0
                ? ToolInput.Create("Name", skill.Name)
                : ToolInput.Create("SkillName", skill.Name, "ResourcePath", resources[index - 1]);
            var fullCallId = $"coordination-inline-{index}";
            var full = await ExecuteAsync(fullCallId, toolName, arguments, defaultBudget, sessionDirectory,
                TrustAudience.Personal);
            Assert.Equal(ToolInvocationOutcomeCategory.Success, full.Context.Invocation.Receipt?.Category);
            Assert.DoesNotContain("[output truncated", full.Result, StringComparison.Ordinal);
            Assert.True(ToolOutputSpillLocation.TryResolve(sessionDirectory, fullCallId, out _, out var inlinePath));
            Assert.False(File.Exists(inlinePath));

            if (index == 0)
            {
                var source = File.ReadAllText(Path.Combine(skill.SkillDirectory, "SKILL.md"));
                Assert.Contains(SkillScanner.ExtractBody(source), full.Result, StringComparison.Ordinal);
                foreach (var resource in resources)
                    Assert.Contains(resource, full.Result, StringComparison.Ordinal);
            }
            else
            {
                var sourcePath = Path.Combine(skill.SkillDirectory, resources[index - 1].Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal($"path: {sourcePath}\n{File.ReadAllText(sourcePath)}", full.Result);
            }

            var spillCallId = $"coordination-spill-{index}";
            var spill = await ExecuteAsync(spillCallId, toolName, arguments, smallBudget, sessionDirectory,
                TrustAudience.Personal);
            Assert.Equal(ToolInvocationOutcomeCategory.Success, spill.Context.Invocation.Receipt?.Category);
            Assert.Contains($"tool_output_read using CallId='{spillCallId}'", spill.Result, StringComparison.Ordinal);
            Assert.DoesNotContain(full.Result, spill.Result, StringComparison.Ordinal);
            Assert.DoesNotContain(sessionDirectory, spill.Result, StringComparison.Ordinal);
            var reconstructed = new StringBuilder();
            var complete = false;
            for (var page = 0; page <= full.Result.Length && !complete; page++)
            {
                var start = reconstructed.Length;
                var window = await ExecuteAsync($"{spillCallId}-page-{page}", ToolOutputReadTool.ToolName,
                    ToolInput.Create("CallId", spillCallId, "Start", start, "Limit", pageLimit),
                    smallBudget, sessionDirectory, TrustAudience.Personal);
                Assert.Equal(ToolInvocationOutcomeCategory.Success, window.Context.Invocation.Receipt?.Category);
                Assert.DoesNotContain("[output truncated", window.Result, StringComparison.Ordinal);
                Assert.True(window.Result.Length <= smallBudget);
                var range = Regex.Match(window.Result,
                    @"\n\[range start=(\d+) end=(\d+); next_start=(none|\d+); complete=(true|false)\]\z",
                    RegexOptions.CultureInvariant);
                Assert.True(range.Success, "The continuation must expose its complete range metadata.");
                var end = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
                Assert.Equal(start, int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture));
                Assert.Equal(end - start, range.Index);
                reconstructed.Append(window.Result.AsSpan(0, range.Index));
                complete = range.Groups[4].Value == "true";
                if (complete)
                    Assert.Equal("none", range.Groups[3].Value);
                else
                {
                    Assert.True(end > start);
                    Assert.Equal(end, int.Parse(range.Groups[3].Value, CultureInfo.InvariantCulture));
                }
            }
            Assert.True(complete);
            Assert.Equal(full.Result, reconstructed.ToString());

            var foreign = await ExecuteAsync($"{spillCallId}-foreign", ToolOutputReadTool.ToolName,
                ToolInput.Create("CallId", spillCallId, "Start", 0, "Limit", pageLimit),
                smallBudget, foreignDirectory, TrustAudience.Personal);
            Assert.Equal(ToolInvocationOutcomeCategory.NotFound, foreign.Context.Invocation.Receipt?.Category);
            await Assert.ThrowsAsync<ToolAccessDeniedException>(() => ExecuteAsync($"{spillCallId}-denied",
                ToolOutputReadTool.ToolName, ToolInput.Create("CallId", spillCallId, "Start", 0, "Limit", pageLimit),
                smallBudget, sessionDirectory, TrustAudience.Public));
        }

        async Task<(string Result, ToolExecutionContext Context)> ExecuteAsync(string callId, string toolName,
            IDictionary<string, object?> arguments, int budget, string directory, TrustAudience audience)
        {
            var context = TestToolExecutionContext.CreateBound(
                directory == sessionDirectory ? "signalr/coordination-session" : "signalr/foreign-session",
                directory, new TestToolExecutionContextOptions
                {
                    Audience = audience,
                    InlineOutputBudget = new InlineOutputBudget(budget)
                });
            var callArguments = new Dictionary<string, object?>(arguments, StringComparer.Ordinal)
            {
                ["_rationale"] = "Verify the complete coordination resource response and its session boundary."
            };
            var result = await executor.ExecuteAsync(new FunctionCallContent(callId, toolName, callArguments),
                context, TestContext.Current.CancellationToken);
            return (result, context);
        }
    }

    private sealed class RejectUnexpectedPromptLoad : IMcpPromptSkillLoader
    {
        public ValueTask<McpPromptSkillLoadResult> LoadAsync(McpPromptSkillSource source,
            IReadOnlyDictionary<string, string>? arguments, ToolInvocationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The inline file skill must not request an MCP prompt.");
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
        await WindowsJunction.CreateAsync(
            paths.SystemSkillsDirectory, externalDirectory, TestContext.Current.CancellationToken);

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
