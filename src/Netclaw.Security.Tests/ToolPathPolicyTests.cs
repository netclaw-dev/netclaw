// -----------------------------------------------------------------------
// <copyright file="ToolPathPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ToolPathPolicyTests
{
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
            "/home/user/.netclaw/config/secrets.json",
            "/home/user/.netclaw/config/webhooks",
            "/home/user/.netclaw/keys",
            "/home/user/.netclaw/netclaw.db",
            "/home/user/.netclaw/netclaw.pid",
            "/home/user/.netclaw/netclaw.lock",
            "/home/user/.netclaw/cache/restart-manifest.json",
            "/home/user/.netclaw/skills/.system",
            "/home/user/.netclaw/skills/.server-feeds",
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

    [Theory]
    [InlineData("/home/user/.netclaw/config/netclaw.json")]
    [InlineData("/home/user/.netclaw/netclaw.db")]
    public void IsReadDenied_allows_non_sensitive_paths(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.False(policy.IsReadDenied(path));
    }

    [Fact]
    public void CommandReferencesDeniedPath_still_allows_ls_of_config_directory()
    {
        // Regression guard: directory-scoped writeDeny entries must not bleed
        // into the shell substring indicator set, otherwise every shell command
        // whose text contains ".netclaw/config" would be rejected.
        var policy = CreateProductionPolicy();
        Assert.False(policy.CommandReferencesDeniedPath("ls ~/.netclaw/config"));
        Assert.False(policy.CommandReferencesDeniedPath("stat ~/.netclaw/config"));
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
