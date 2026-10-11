// -----------------------------------------------------------------------
// <copyright file="ToolPathPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security.Authorization.Filesystem;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ToolPathPolicyTests
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [Theory]
    [InlineData("relative/path", ShellPathStyle.Posix)]
    [InlineData("/work/line\nbreak", ShellPathStyle.Posix)]
    [InlineData("relative\\path", ShellPathStyle.Windows)]
    [InlineData("C:\\work\\line\nbreak", ShellPathStyle.Windows)]
    [InlineData(@"C:\work", (ShellPathStyle)999)]
    public void Canonical_path_rejects_invalid_values(
        string path,
        ShellPathStyle pathStyle)
    {
        Assert.False(CanonicalPath.TryCreate(path, relativeBase: null, pathStyle, out _));
    }

    [Theory]
    [InlineData("/work/../external/file.txt", ShellPathStyle.Posix, "/external/file.txt")]
    [InlineData(@"C:\work\..\external\file.txt", ShellPathStyle.Windows, @"C:\external\file.txt")]
    [InlineData(@"\\server\share\work\..\file.txt", ShellPathStyle.Windows, @"\\server\share\file.txt")]
    public void Canonical_path_uses_declared_style_on_every_host(
        string value,
        ShellPathStyle pathStyle,
        string expected)
    {
        Assert.True(CanonicalPath.TryCreate(value, relativeBase: null, pathStyle, out var path));
        Assert.Equal(expected, path.Value);
    }

    [Fact]
    public void Projected_path_check_rejects_a_mismatched_path_style()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
        var policy = new ToolPathPolicy(environment, [@"C:\protected"]);
        Assert.True(CanonicalPath.TryCreate(
            "/protected",
            relativeBase: null,
            ShellPathStyle.Posix,
            out var path));

        Assert.True(policy.IsShellDeniedProjectedPath(path));
    }

    // Path traversal (..) must normalize to the denied path before matching.
    // Matching is case-insensitive.
    [Theory]
    [InlineData("/home/user/.netclaw/config/secrets.json", "/home/user/.netclaw/config/secrets.json", true)]
    [InlineData("/home/user/.netclaw/config/secrets.json", "/home/user/.netclaw/config/netclaw.json", false)]
    [InlineData("/home/user/.netclaw/config/secrets.json", "/home/user/.netclaw/config/../config/secrets.json", true)]
    [InlineData("/home/user/.netclaw/config/Secrets.json", "/home/user/.netclaw/config/secrets.json", true)]
    [InlineData("/home/user/.netclaw/keys", "/home/user/.netclaw/keys/keyring.xml", true)]
    [InlineData("/home/user/.netclaw/keys", "/home/user/.netclaw/keys-backup/data.txt", false)]
    [InlineData("/home/user/.netclaw/config/webhooks", "/home/user/.netclaw/config/webhooks/github-issues.json", true)]
    public void Write_protection_matches_denied_paths(string deniedPath, string testPath, bool expected)
    {
        var policy = new ToolPathPolicy([deniedPath]);
        Assert.Equal(expected, policy.FileSystem.IsProtected(testPath, PathOperation.Write));
    }

    [Fact]
    public void Write_protection_returns_false_for_empty_path()
    {
        var policy = new ToolPathPolicy(["/some/path"]);
        Assert.False(policy.FileSystem.IsProtected("", PathOperation.Write));
        Assert.False(policy.FileSystem.IsProtected("  ", PathOperation.Write));
    }

    [Theory]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "cat /home/user/.netclaw/config/secrets.json", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "cat /home/user/.netclaw/config/secrets.json | jq .", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "ls -la /tmp", false)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "echo hello", false)]
    [InlineData(new[] { "/some/path" }, "", false)]
    [InlineData(new[] { "/some/path" }, "  ", false)]
    [InlineData(new[] { "/home/user/.netclaw/keys" }, "ls ~/.netclaw/keys", true)]
    [InlineData(new[] { "/home/user/.netclaw/keys" }, "tar czf /tmp/k.tgz ~/.netclaw/keys", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/webhooks" }, "cat ~/.netclaw/config/webhooks/github-issues.json", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/webhooks" }, "tar czf /tmp/webhooks.tgz ~/.netclaw/config/webhooks", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "cat ~/.netclaw/config/*.json", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "jq . ~/.netclaw/config/*.json", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "tar czf /tmp/netclaw-config.tgz ~/.netclaw/config", true)]
    // With no live config directory in the lists, the default layout keeps its hint with a high-risk verb.
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "cat ~/.netclaw/config/netclaw.json", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "jq .Tools ~/.netclaw/config/tool-approvals.json", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "cat ~/.netclaw/config/../config/secrets.json", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "grep -r token ~/.netclaw/config/", true)]
    [InlineData(new[] { "/home/user/.netclaw/config/secrets.json" }, "cat ~/.netclaw/config/net*.json", true)]
    public void CommandReferencesDeniedPath_matches_denied_paths_in_commands(
        string[] deniedPaths,
        string command,
        bool expected)
    {
        var policy = new ToolPathPolicy(deniedPaths);
        Assert.Equal(expected, policy.CommandReferencesDeniedPath(command));
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
    public void CommandReferencesDeniedPath_checks_the_working_directory()
    {
        var policy = new ToolPathPolicy(["/protected/catalog"]);

        Assert.True(policy.CommandReferencesDeniedPath("find", "/protected/catalog"));
        Assert.True(policy.CommandReferencesDeniedPath("find", "/protected/catalog/mcp"));
        Assert.False(policy.CommandReferencesDeniedPath("find", "/work"));
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
    public void Multiple_denied_paths()
    {
        var policy = new ToolPathPolicy(["/path/a", "/path/b"]);
        Assert.True(policy.FileSystem.IsProtected("/path/a", PathOperation.Write));
        Assert.True(policy.FileSystem.IsProtected("/path/b", PathOperation.Write));
        Assert.False(policy.FileSystem.IsProtected("/path/c", PathOperation.Write));
    }

    [Fact]
    public void CommandReferencesDeniedPath_detects_home_shorthand()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var policy = new ToolPathPolicy([Path.Combine(home, ".netclaw", "config", "secrets.json")]);

        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.netclaw/config/secrets.json"));
        Assert.True(policy.CommandReferencesDeniedPath("cat $HOME/.netclaw/config/secrets.json"));
    }

    // A home-anchored entry shows in command text only in its anchored spellings
    // and its absolute path, each ending at a name boundary. A bare name, a
    // "/name" substring or a longer name would also deny a workspace path, a
    // pattern or a neighbour file. The spellings are Bash and PowerShell forms
    // and compare without case, with backslashes read as slashes.
    private static readonly ToolPathPolicy.HomeAnchor[] PosixAnchors =
    [
        new("/home/u", ToolPathPolicy.HomeSpellings)
    ];

    private static readonly ToolPathPolicy.HomeAnchor[] WindowsAnchors =
    [
        new(@"C:\Users\u", ToolPathPolicy.HomeSpellings),
        new(@"C:\Users\u\AppData\Roaming", ToolPathPolicy.AppDataSpellings),
        new(@"C:\ProgramData", ToolPathPolicy.ProgramDataSpellings),
    ];

    private static bool AnchoredMatch(
        IReadOnlyList<ToolPathPolicy.HomeAnchor> anchors,
        string[] entries,
        string command)
    {
        var text = command.Replace('\\', '/');
        return ToolPathPolicy.BuildAnchoredIndicators(entries, anchors).Any(regex => regex.IsMatch(text));
    }

    [Theory]
    [InlineData("cat ~/.ssh/id_ed25519", true)]
    [InlineData("cat $HOME/.ssh", true)]
    [InlineData("cat ${HOME}/.ssh/x", true)]
    [InlineData("ls \"~/.ssh\"", true)]
    [InlineData("docker run -v $HOME/.aws:/root/.aws:ro img", true)]
    [InlineData("cat /home/u/.ssh/id_ed25519", true)]
    [InlineData("cat ~/.docker/config.json", true)]
    [InlineData("cat ~/.netrc", true)]
    [InlineData("cat ~/.config/gh/hosts.yml", true)]
    [InlineData("cat ~/.SSH/id", true)]
    [InlineData("cat ~/.sshrc", false)]
    [InlineData("cat ~/.awsome/x", false)]
    [InlineData("cat ~/.ssh-backup/x", false)]
    [InlineData("cat /home/u/.sshrc", false)]
    [InlineData("cat ~/.docker/config.json.bak", false)]
    [InlineData("cat ~/.docker/contexts/meta.json", false)]
    [InlineData("cat ~/.netrc-example.md", false)]
    [InlineData("cat ~/.config/ghx/x", false)]
    [InlineData("cat app/.ssh/config", false)]
    [InlineData("cat tsconfig.json", false)]
    [InlineData("grep -rn '\\.ssh' .", false)]
    [InlineData("grep -c 'docs\\.aws\\.amazon\\.com' README.md", false)]
    [InlineData("curl https://docs.aws.amazon.com", false)]
    public void Posix_home_anchored_entry_matches_only_its_anchored_spellings(string command, bool denied)
    {
        string[] entries =
        [
            "/home/u/.ssh", "/home/u/.aws", "/home/u/.docker/config.json", "/home/u/.netrc", "/home/u/.config/gh"
        ];

        Assert.Equal(denied, AnchoredMatch(PosixAnchors, entries, command));
    }

    [Theory]
    [InlineData(@"Get-Content ~\.ssh\id_ed25519", true)]
    [InlineData(@"gc $HOME\.ssh\id_ed25519", true)]
    [InlineData(@"cat $env:USERPROFILE\.aws\credentials", true)]
    [InlineData(@"cat ${env:USERPROFILE}\.aws\credentials", true)]
    [InlineData(@"gc $ENV:userprofile\.SSH\x", true)]
    [InlineData(@"gc ~/.ssh/x", true)]
    [InlineData(@"type %USERPROFILE%\.ssh\id_ed25519", true)]
    [InlineData(@"cmd /c type %APPDATA%\gcloud\credentials.db", true)]
    [InlineData(@"type %PROGRAMDATA%\ssh\ssh_host_rsa_key", true)]
    [InlineData(@"gc C:\Users\u\.ssh\id_ed25519", true)]
    [InlineData(@"gc c:/users/U/.SSH/id_ed25519", true)]
    [InlineData(@"gc $env:USERPROFILE\.docker\config.json", true)]
    [InlineData(@"gc $env:USERPROFILE\_netrc", true)]
    [InlineData(@"gc $env:APPDATA\gcloud\credentials.db", true)]
    [InlineData(@"gc ${env:APPDATA}\GitHub CLI\hosts.yml", true)]
    [InlineData(@"gc $env:PROGRAMDATA\ssh\administrators_authorized_keys", true)]
    [InlineData(@"Select-String '\.ssh' *", false)]
    [InlineData(@"gc infra\.ssh\config", false)]
    [InlineData(@"gc .devcontainer\.aws\config", false)]
    [InlineData(@"gc C:\Users\u\.sshrc", false)]
    [InlineData(@"gc $env:APPDATA\gcloudx\x", false)]
    [InlineData(@"gc $env:USERPROFILE\projects\app\.aws\config", false)]
    public void Windows_home_anchored_entry_matches_only_its_anchored_spellings(string command, bool denied)
    {
        string[] entries =
        [
            @"C:\Users\u\.ssh", @"C:\Users\u\.aws", @"C:\Users\u\.docker\config.json", @"C:\Users\u\_netrc",
            @"C:\Users\u\AppData\Roaming\gcloud", @"C:\Users\u\AppData\Roaming\GitHub CLI", @"C:\ProgramData\ssh"
        ];

        Assert.Equal(denied, AnchoredMatch(WindowsAnchors, entries, command));
    }

    // The policy builds the anchors from the launch home of this host.
    [Theory]
    [InlineData("cat ~/.vault/token", true)]
    [InlineData("cat $HOME/.vault/token", true)]
    [InlineData("cat app/.vault/token", false)]
    [InlineData("cat ~/.vaultrc", false)]
    [InlineData("grep -rn '\\.vault' .", false)]
    [InlineData("curl https://docs.vault.example.com", false)]
    public void A_home_anchored_entry_of_the_policy_uses_the_launch_home(string command, bool denied)
    {
        var entry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vault");
        var policy = new ToolPathPolicy(
            ShellExecutionEnvironmentDefaults.Bash,
            [entry],
            [entry],
            [entry],
            [entry]);

        Assert.Equal(denied, policy.CommandReferencesDeniedPath(command));
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
        };
        var readDeny = new[]
        {
            "/home/user/.netclaw/config/secrets.json",
            "/home/user/.netclaw/keys",
            "/home/user/.netclaw/config/webhooks",
            "/home/user/.netclaw/netclaw.db",
            "/home/user/.netclaw/netclaw.db-wal",
            "/home/user/.netclaw/netclaw.db-shm",
            "/home/user/.netclaw/netclaw.db-journal",
            "/home/user/.netclaw/netclaw.pid",
            "/home/user/.netclaw/netclaw.lock",
            "/home/user/.netclaw/cache/restart-manifest.json",
        };
        var shellIndicators = new[]
        {
            // ConfigDirectory is a directory-scoped shell indicator in production.
            // Structured file reads use the independent read deny list.
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
    public void Write_protection_blocks_control_plane_files(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Write));
    }

    [Theory]
    [InlineData("/home/user/.netclaw/config/netclaw.json")]
    [InlineData("/home/user/.netclaw/config/devices.json")]
    [InlineData("/home/user/.netclaw/config/tool-approvals.json")]
    [InlineData("/home/user/.netclaw/config/mcp-oauth-metadata.json")]
    [InlineData("/home/user/.netclaw/identity/SOUL.md")]
    [InlineData("/home/user/.netclaw/identity/AGENTS.md")]
    [InlineData("/home/user/.netclaw/skills/my-skill/SKILL.md")]
    [InlineData("/home/user/.netclaw/skills/.system/my-skill/SKILL.md")]
    [InlineData("/home/user/.netclaw/skills/.server-feeds/my-feed/feed-skill/SKILL.md")]
    [InlineData("/tmp/foo.json")]
    [InlineData("/home/user/Documents/notes.txt")]
    public void Write_protection_allows_safe_write_paths(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.False(policy.FileSystem.IsProtected(path, PathOperation.Write));
    }

    [Theory]
    [InlineData("/home/user/.netclaw/config/secrets.json")]
    [InlineData("/home/user/.netclaw/keys/keyring.xml")]
    [InlineData("/home/user/.netclaw/config/webhooks/github-issues.json")]
    public void Read_protection_blocks_sensitive_paths(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Read));
    }

    [Theory]
    [InlineData("/home/user/.netclaw/netclaw.db")]
    [InlineData("/home/user/.netclaw/netclaw.db-wal")]
    [InlineData("/home/user/.netclaw/netclaw.db-shm")]
    [InlineData("/home/user/.netclaw/netclaw.db-journal")]
    [InlineData("/home/user/.netclaw/netclaw.pid")]
    [InlineData("/home/user/.netclaw/netclaw.lock")]
    [InlineData("/home/user/.netclaw/cache/restart-manifest.json")]
    public void Read_protection_blocks_control_plane_files(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Read));
    }

    [Fact]
    public void Read_protection_allows_ordinary_config_while_shell_remains_denied()
    {
        var policy = CreateProductionPolicy();
        const string configPath = "/home/user/.netclaw/config/netclaw.json";

        Assert.False(policy.FileSystem.IsProtected(configPath, PathOperation.Read));
        Assert.True(policy.CommandReferencesDeniedPath($"cat {configPath}"));
    }

    public enum SymlinkTraversalShape
    {
        SingleSymlinkedDirectory,
        MultiDepthSymlinkChain,
        DotDotTraversalAfterResolvedLink,
    }

    // Regression (#1724): a symlinked INTERMEDIATE directory into a denied
    // location must not bypass read protection. Shell catches this via
    // TryResolveSymlinksInPath; the read side must too, since interactive
    // Personal reads have read protection as their sole backstop.
    [Theory]
    [InlineData(SymlinkTraversalShape.SingleSymlinkedDirectory)]
    [InlineData(SymlinkTraversalShape.MultiDepthSymlinkChain)]
    [InlineData(SymlinkTraversalShape.DotDotTraversalAfterResolvedLink)]
    public void Read_protection_blocks_symlinked_directory_traversal(SymlinkTraversalShape shape)
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

            Assert.True(policy.FileSystem.IsProtected(viaLink, PathOperation.Read));
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

    // Structured reads use their own explicit deny list. Shell indicators do
    // not widen that list.
    [Theory]
    [InlineData("/home/user/repositories/foo.cs")]
    [InlineData("/tmp/notes.txt")]
    [InlineData("/home/user/downloads/report.pdf")]
    public void Read_protection_allows_non_sensitive_paths(string path)
    {
        var policy = CreateProductionPolicy();
        Assert.False(policy.FileSystem.IsProtected(path, PathOperation.Read));
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

    [Theory]
    [InlineData("tool-approvals.json")]
    [InlineData("netclaw.json")]
    [InlineData("hard-deny-overrides.json")]
    [InlineData("future/subsystem/settings.json")]
    public void Write_protection_blocks_descendants_of_config_dir(string relativePath)
    {
        var configDir = "/home/user/.netclaw/config";
        var policy = new ToolPathPolicy([configDir]);
        var segments = relativePath.Split('/');

        Assert.True(policy.FileSystem.IsProtected(Path.Combine([configDir, .. segments]), PathOperation.Write));
    }

    [Fact]
    public void Write_protection_does_not_block_sibling_of_config_dir()
    {
        // boundary safety: ~/.netclaw/configbackup/ must not be denied just
        // because its name shares a prefix with ~/.netclaw/config/.
        var policy = new ToolPathPolicy(["/home/user/.netclaw/config"]);

        Assert.False(policy.FileSystem.IsProtected("/home/user/.netclaw/configbackup/file.json", PathOperation.Write));
    }

    [Theory]
    [InlineData("echo {} > {configDir}/tool-approvals.json")]
    [InlineData("echo content | tee {configDir}/netclaw.json")]
    public void CommandReferencesDeniedPath_detects_writes_into_config_dir(string commandTemplate)
    {
        var configDir = "/home/user/.netclaw/config";
        var policy = new ToolPathPolicy(deniedPaths: [configDir]);
        var command = commandTemplate.Replace("{configDir}", configDir);

        Assert.True(policy.CommandReferencesDeniedPath(command));
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
