// -----------------------------------------------------------------------
// <copyright file="ToolPathPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ToolPathPolicyTests
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [Fact]
    public void IsDenied_blocks_exact_match()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);
        Assert.True(policy.IsDenied("/home/user/.netclaw/config/secrets.json"));
    }

    [Fact]
    public void IsDenied_allows_non_matching_path()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);
        Assert.False(policy.IsDenied("/home/user/.netclaw/config/netclaw.json"));
    }

    [Fact]
    public void IsDenied_normalizes_path_traversal()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);
        // Path with .. that resolves to the denied path
        Assert.True(policy.IsDenied("/home/user/.netclaw/config/../config/secrets.json"));
    }

    [Fact]
    public void IsDenied_case_insensitive()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/Secrets.json"]);
        Assert.True(policy.IsDenied("/home/user/.netclaw/config/secrets.json"));
    }

    [Fact]
    public void IsDenied_returns_false_for_empty_path()
    {
        var policy = new ToolPathPolicy(["/some/path"]);
        Assert.False(policy.IsDenied(""));
        Assert.False(policy.IsDenied("  "));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_path_in_command()
    {
        var secretsPath = "/home/user/.netclaw/config/secrets.json";
        var policy = new ToolPathPolicy([secretsPath]);

        Assert.True(policy.CommandReferencesDeniedPath($"cat {secretsPath}"));
        Assert.True(policy.CommandReferencesDeniedPath($"cat {secretsPath} | jq ."));
    }

    [Fact]
    public void CommandReferencesDeniedPath_allows_safe_commands()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);
        Assert.False(policy.CommandReferencesDeniedPath("ls -la /tmp"));
        Assert.False(policy.CommandReferencesDeniedPath("echo hello"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_checks_finite_authored_filesystem_values()
    {
        var policy = new ToolPathPolicy(["/work/src/B.cs"]);
        const string command =
            "for f in src/A.cs src/B.cs; do cat /work/$f; done";

        Assert.True(policy.CommandReferencesDeniedPath(command, "/work"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_checks_native_power_shell_path()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var policy = new ToolPathPolicy(environment, [@"C:\protected\config"]);
        const string command = @"Get-Content C:\protected\config\file.txt";

        Assert.True(policy.CommandReferencesDeniedPath(command, @"C:\work"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_returns_false_for_empty()
    {
        var policy = new ToolPathPolicy(["/some/path"]);
        Assert.False(policy.CommandReferencesDeniedPath(""));
        Assert.False(policy.CommandReferencesDeniedPath("  "));
    }

    [Fact]
    public void Multiple_denied_paths()
    {
        var policy = new ToolPathPolicy(["/path/a", "/path/b"]);
        Assert.True(policy.IsDenied("/path/a"));
        Assert.True(policy.IsDenied("/path/b"));
        Assert.False(policy.IsDenied("/path/c"));
    }

    [Fact]
    public void IsDenied_blocks_children_of_denied_directory()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/keys"]);
        Assert.True(policy.IsDenied("/home/user/.netclaw/keys/keyring.xml"));
    }

    [Fact]
    public void IsDenied_does_not_match_prefix_without_path_boundary()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/keys"]);
        Assert.False(policy.IsDenied("/home/user/.netclaw/keys-backup/data.txt"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_home_shorthand()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var policy = new ToolPathPolicy([Path.Combine(home, ".netclaw", "config", "secrets.json")]);

        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/config/secrets.json"));
        Assert.True(policy.CommandReferencesDeniedPath("cat $HOME/.netclaw/config/secrets.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_keys_directory_access()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/keys"]);

        Assert.True(policy.CommandReferencesDeniedPath("ls ~/.netclaw/keys"));
        Assert.True(policy.CommandReferencesDeniedPath("tar czf /tmp/k.tgz ~/.netclaw/keys"));
    }

    [Fact]
    public void IsDenied_blocks_children_of_webhooks_directory()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/webhooks"]);

        Assert.True(policy.IsDenied("/home/user/.netclaw/config/webhooks/github-issues.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_webhooks_directory_access()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/webhooks"]);

        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/config/webhooks/github-issues.json"));
        Assert.True(policy.CommandReferencesDeniedPath("tar czf /tmp/webhooks.tgz ~/.netclaw/config/webhooks"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_high_risk_glob_in_config_directory()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);

        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/config/*.json"));
        Assert.True(policy.CommandReferencesDeniedPath("jq . ~/.netclaw/config/*.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_high_risk_archive_of_config_directory()
    {
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);

        Assert.True(policy.CommandReferencesDeniedPath("tar czf /tmp/netclaw-config.tgz ~/.netclaw/config"));
    }

    private static ToolPathPolicy CreateProductionPolicy()
    {
        var writeDeny = new[]
        {
            "/home/user/.netclaw/config/secrets.json",
            "/home/user/.netclaw/keys",
            "/home/user/.netclaw/netclaw.db",
            "/home/user/.netclaw/netclaw.pid",
            "/home/user/.netclaw/netclaw.lock",
            "/home/user/.netclaw/cache/restart-manifest.json",
            "/home/user/.netclaw/skills/.system",
            "/home/user/.netclaw/skills/.server-feeds",
        };
        var readDeny = new[]
        {
            "/home/user/.netclaw/config/secrets.json",
            "/home/user/.netclaw/keys",
            "/home/user/.netclaw/config/webhooks",
        };
        var shellIndicators = new[]
        {
            // ConfigDirectory is a directory-scoped shell indicator in production
            // (src/Netclaw.Daemon/Program.cs), so the whole config dir is denied
            // for shell references AND (via the IsReadDenied union) for reads.
            "/home/user/.netclaw/config",
            "/home/user/.netclaw/config/secrets.json",
            "/home/user/.netclaw/config/webhooks",
            "/home/user/.netclaw/keys",
            "/home/user/.netclaw/netclaw.db",
            // SQLite sidecars mirror production (Program.cs) — the path-boundary
            // matcher would otherwise allow netclaw.db-wal/journal/shm reads.
            "/home/user/.netclaw/netclaw.db-wal",
            "/home/user/.netclaw/netclaw.db-shm",
            "/home/user/.netclaw/netclaw.db-journal",
            "/home/user/.netclaw/netclaw.pid",
            "/home/user/.netclaw/netclaw.lock",
            "/home/user/.netclaw/cache/restart-manifest.json",
        };
        return new ToolPathPolicy(writeDeny, readDeny, shellIndicators);
    }

    [Theory]
    [InlineData("/home/user/.netclaw/netclaw.db")]
    [InlineData("/home/user/.netclaw/netclaw.pid")]
    [InlineData("/home/user/.netclaw/netclaw.lock")]
    [InlineData("/home/user/.netclaw/cache/restart-manifest.json")]
    [InlineData("/home/user/.netclaw/skills/.system/my-skill/SKILL.md")]
    [InlineData("/home/user/.netclaw/skills/.server-feeds/my-feed/feed-skill/SKILL.md")]
    public void IsDenied_blocks_control_plane_files(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.True(policy.IsDenied(path));
    }

    [Theory]
    [InlineData("/home/user/.netclaw/config/netclaw.json")]
    [InlineData("/home/user/.netclaw/config/devices.json")]
    [InlineData("/home/user/.netclaw/config/tool-approvals.json")]
    [InlineData("/home/user/.netclaw/config/mcp-oauth-metadata.json")]
    [InlineData("/home/user/.netclaw/identity/SOUL.md")]
    [InlineData("/home/user/.netclaw/identity/AGENTS.md")]
    [InlineData("/home/user/.netclaw/skills/my-skill/SKILL.md")]
    [InlineData("/tmp/foo.json")]
    [InlineData("/home/user/Documents/notes.txt")]
    public void IsDenied_allows_safe_write_paths(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.False(policy.IsDenied(path));
    }

    [Theory]
    [InlineData("/home/user/.netclaw/config/secrets.json")]
    [InlineData("/home/user/.netclaw/keys/keyring.xml")]
    [InlineData("/home/user/.netclaw/config/webhooks/github-issues.json")]
    public void IsReadDenied_blocks_sensitive_paths(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.True(policy.IsReadDenied(path));
    }

    // The read deny surface is the union of the read deny list and the shell
    // indicator list, so read tools cannot reach control-plane lifecycle files
    // that shell cannot even reference (#1724).
    [Theory]
    [InlineData("/home/user/.netclaw/config/netclaw.json")]
    [InlineData("/home/user/.netclaw/netclaw.db")]
    [InlineData("/home/user/.netclaw/netclaw.db-wal")]
    [InlineData("/home/user/.netclaw/netclaw.db-shm")]
    [InlineData("/home/user/.netclaw/netclaw.db-journal")]
    [InlineData("/home/user/.netclaw/netclaw.pid")]
    [InlineData("/home/user/.netclaw/netclaw.lock")]
    [InlineData("/home/user/.netclaw/cache/restart-manifest.json")]
    public void IsReadDenied_blocks_control_plane_files(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.True(policy.IsReadDenied(path));
    }

    public enum SymlinkTraversalShape
    {
        SingleSymlinkedDirectory,
        MultiDepthSymlinkChain,
        DotDotTraversalAfterResolvedLink,
    }

    // Regression (#1724): a symlinked INTERMEDIATE directory into a denied
    // location must not bypass IsReadDenied. Shell catches this via
    // TryResolveSymlinksInPath; the read side must too, since interactive
    // Personal reads have IsReadDenied as their sole backstop.
    [Theory]
    [InlineData(SymlinkTraversalShape.SingleSymlinkedDirectory)]
    [InlineData(SymlinkTraversalShape.MultiDepthSymlinkChain)]
    [InlineData(SymlinkTraversalShape.DotDotTraversalAfterResolvedLink)]
    public void IsReadDenied_blocks_symlinked_directory_traversal(SymlinkTraversalShape shape)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"netclaw-symlink-{Guid.NewGuid():N}");
        var deniedDir = Path.Combine(scratch, "denied");
        Directory.CreateDirectory(deniedDir);
        File.WriteAllText(Path.Combine(deniedDir, "netclaw.json"), """{"secret":true}""");

        var createdLinks = new List<string>();

        try
        {
            string viaLink;

            switch (shape)
            {
                case SymlinkTraversalShape.SingleSymlinkedDirectory:
                    {
                        var linkDir = Path.Combine(scratch, "link");
                        Directory.CreateSymbolicLink(linkDir, deniedDir);
                        createdLinks.Add(linkDir);

                        // Lexically this path lives in scratch/link, outside any
                        // denied root — only segment-walk symlink resolution
                        // catches it.
                        viaLink = Path.Combine(linkDir, "netclaw.json");
                        break;
                    }

                case SymlinkTraversalShape.MultiDepthSymlinkChain:
                    {
                        // linkA -> linkB -> deniedDir. A resolver that only
                        // follows one hop would stop at linkB; the walk must
                        // reach the final real target.
                        var linkB = Path.Combine(scratch, "linkB");
                        var linkA = Path.Combine(scratch, "linkA");
                        Directory.CreateSymbolicLink(linkB, deniedDir);
                        Directory.CreateSymbolicLink(linkA, linkB);
                        createdLinks.Add(linkB);
                        createdLinks.Add(linkA);

                        viaLink = Path.Combine(linkA, "netclaw.json");
                        break;
                    }

                case SymlinkTraversalShape.DotDotTraversalAfterResolvedLink:
                    {
                        var linkDir = Path.Combine(scratch, "link");
                        Directory.CreateSymbolicLink(linkDir, deniedDir);
                        createdLinks.Add(linkDir);

                        // "nested" need not exist: Path.GetFullPath collapses the
                        // ".." lexically before any symlink is resolved, leaving
                        // "link/netclaw.json" — the link segment itself survives
                        // the collapse untouched, so resolution still lands
                        // inside deniedDir. Locks in that a decoy ".." placed
                        // after the link cannot be used to dodge the walk.
                        viaLink = Path.Combine(linkDir, "nested", "..", "netclaw.json");
                        break;
                    }

                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
            }

            // Deny the REAL deniedDir (fixture paths don't exist on disk, and
            // symlink resolution needs an on-disk target to resolve).
            var policy = new ToolPathPolicy([deniedDir]);

            Assert.True(policy.IsReadDenied(viaLink));
        }
        catch (UnauthorizedAccessException)
        {
            return; // Windows without developer mode
        }
        finally
        {
            foreach (var link in createdLinks)
            {
                if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null)
                    Directory.Delete(link);
            }

            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    // The fixture mirrors production (Program.cs): ConfigDirectory is a
    // directory-scoped shell indicator, so the whole config dir is read-denied
    // via the IsReadDenied union. Sidecar files (db-wal/db-shm/db-journal) are
    // also in the fixture, matching the production shell indicator list.
    [Theory]
    [InlineData("/home/user/repositories/foo.cs")]
    [InlineData("/tmp/notes.txt")]
    [InlineData("/home/user/downloads/report.pdf")]
    public void IsReadDenied_allows_non_sensitive_paths(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.False(policy.IsReadDenied(path));
    }

    [Fact]
    public void CommandReferencesDeniedPath_denies_ls_of_config_directory()
    {
        // Production includes ConfigDirectory in the shell indicator list
        // (src/Netclaw.Daemon/Program.cs), so `ls ~/.netclaw/config` is denied
        // by the substring indicator scan. This mirrors production behavior;
        // the fixture now includes ConfigDirectory to match.
        var policy = CreateProductionPolicy();
        Assert.True(policy.CommandReferencesDeniedPath("ls ~/.netclaw/config"));
        Assert.True(policy.CommandReferencesDeniedPath("stat ~/.netclaw/config"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_still_blocks_cat_of_secrets_json()
    {
        var policy = CreateProductionPolicy();
        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/config/secrets.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_blocks_control_plane_lifecycle_files()
    {
        var policy = CreateProductionPolicy();

        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/netclaw.db"));
        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/netclaw.pid"));
        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/netclaw.lock"));
        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/cache/restart-manifest.json"));
    }

    [Theory]
    [InlineData("bash /home/user/.netclaw/skills/.system/my-skill/tools/check")]
    [InlineData("/home/user/.netclaw/skills/.server-feeds/my-feed/feed-skill/tools/check")]
    public void CommandReferencesDeniedPath_allows_synced_skill_resource_execution(string command)
    {
        var policy = CreateProductionPolicy();

        Assert.False(policy.CommandReferencesDeniedPath(command));
    }

    // Regression: directory-scoped approvals let a user grant a single root
    // (e.g., /home/user/safe/) once, after which all subsequent shell commands
    // under that root auto-approve. The design promises that ToolPathPolicy
    // remains a backstop and still blocks protected-path access even after a
    // root grant. This test verifies that promise specifically against the
    // symlink-escalation case: an attacker (or a hallucinating agent) plants a
    // symlink inside the approved root that points at a protected path. The
    // approval gate sees a path "within" the approved root and waves it
    // through, so ToolPathPolicy MUST resolve symlinks during command
    // inspection or the layered defense is paper-only.
    [Fact]
    public void CommandReferencesDeniedPath_blocks_symlink_escalation_into_protected_path()
    {
        // CreateSymbolicLink without elevation requires Developer Mode on
        // Windows; the underlying gap is platform-agnostic but the test
        // surface is unreliable there. POSIX is sufficient for regression.
        if (OperatingSystem.IsWindows())
            return;

        var safeRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(safeRoot);
        var leak = Path.Combine(safeRoot, "leak");
        Directory.CreateSymbolicLink(leak, "/etc");

        try
        {
            var policy = new ToolPathPolicy(deniedPaths: ["/etc"]);
            var command = $"cat {leak}/passwd";

            Assert.True(
                policy.CommandReferencesDeniedPath(command),
                $"ToolPathPolicy must resolve symlinks when inspecting shell commands; otherwise a directory-scoped approval for {safeRoot}/ becomes a read primitive for any protected path reachable via planted symlinks. Command under test: {command}");
        }
        finally
        {
            // Delete the symlink itself, not its target. File.Delete on a
            // symlink to a directory removes the link without touching /etc.
            File.Delete(leak);
            Directory.Delete(safeRoot);
        }
    }

    // The following tests cover the prompt-injection defense added by the
    // approval-policy-trust-zones change (task 10.8): extending the write-
    // and shell-deny lists to cover ~/.netclaw/config/ so an injected payload
    // cannot instruct the agent to rewrite tool-approvals.json or
    // hard-deny-overrides.json and grant itself global trust.

    [Fact]
    public void IsDenied_blocks_tool_approvals_json_under_config_dir()
    {
        var configDir = "/home/user/.netclaw/config";
        var policy = new ToolPathPolicy([configDir]);

        Assert.True(policy.IsDenied(Path.Combine(configDir, "tool-approvals.json")));
    }

    [Fact]
    public void IsDenied_blocks_netclaw_json_under_config_dir()
    {
        var configDir = "/home/user/.netclaw/config";
        var policy = new ToolPathPolicy([configDir]);

        Assert.True(policy.IsDenied(Path.Combine(configDir, "netclaw.json")));
    }

    [Fact]
    public void IsDenied_blocks_hard_deny_overrides_under_config_dir()
    {
        var configDir = "/home/user/.netclaw/config";
        var policy = new ToolPathPolicy([configDir]);

        Assert.True(policy.IsDenied(Path.Combine(configDir, "hard-deny-overrides.json")));
    }

    [Fact]
    public void IsDenied_blocks_arbitrary_descendant_of_config_dir()
    {
        var configDir = "/home/user/.netclaw/config";
        var policy = new ToolPathPolicy([configDir]);

        Assert.True(policy.IsDenied(Path.Combine(configDir, "future", "subsystem", "settings.json")));
    }

    [Fact]
    public void IsDenied_does_not_block_sibling_of_config_dir()
    {
        // boundary safety: ~/.netclaw/configbackup/ must not be denied just
        // because its name shares a prefix with ~/.netclaw/config/.
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config"]);

        Assert.False(policy.IsDenied("/home/user/.netclaw/configbackup/file.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_shell_redirect_to_config_file()
    {
        var configDir = "/home/user/.netclaw/config";
        var policy = new ToolPathPolicy(deniedPaths: [configDir]);

        Assert.True(policy.CommandReferencesDeniedPath(
            $"echo {{}} > {configDir}/tool-approvals.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_tee_to_config_file()
    {
        var configDir = "/home/user/.netclaw/config";
        var policy = new ToolPathPolicy(deniedPaths: [configDir]);

        Assert.True(policy.CommandReferencesDeniedPath(
            $"echo content | tee {configDir}/netclaw.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_cat_of_config_file()
    {
        // Reading config files isn't in the readDeny list, but the shell
        // indicator list also blocks shell access (in the daemon wiring
        // ConfigDirectory is in shellIndicatorList too).
        var configDir = "/home/user/.netclaw/config";
        var policy = new ToolPathPolicy(deniedPaths: [configDir]);

        Assert.True(policy.CommandReferencesDeniedPath(
            $"cat {configDir}/tool-approvals.json"));
    }

    [Fact]
    public void CommandReferencesDeniedPath_resolves_symlink_routed_to_config()
    {
        // Skip on platforms where symlinks aren't easily creatable.
        if (Environment.OSVersion.Platform != PlatformID.Unix
            && Environment.OSVersion.Platform != PlatformID.MacOSX)
            return;

        var configDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "tool-approvals.json"), "{}");

        var scratchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);
        var leakLink = Path.Combine(scratchDir, "leak");
        File.CreateSymbolicLink(leakLink, Path.Combine(configDir, "tool-approvals.json"));

        try
        {
            var policy = new ToolPathPolicy(deniedPaths: [configDir]);
            var command = $"cat {leakLink}";

            Assert.True(
                policy.CommandReferencesDeniedPath(command),
                $"ToolPathPolicy must resolve symlinks routed at config files; planted symlink in writable scratch dir would otherwise become a read primitive for security-critical config. Command under test: {command}");
        }
        finally
        {
            File.Delete(leakLink);
            Directory.Delete(scratchDir);
            File.Delete(Path.Combine(configDir, "tool-approvals.json"));
            Directory.Delete(configDir);
            Directory.Delete(Path.GetDirectoryName(configDir)!);
        }
    }
}
