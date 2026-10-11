// -----------------------------------------------------------------------
// <copyright file="DaemonToolPathPolicyFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using Netclaw.Tests.Utilities;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class DaemonToolPathPolicyFactoryTests
{
    // Owner decision D6: the agent may read each file under the config
    // directory, with a file tool and with a read-only shell program, except
    // secrets.json and the webhook route files. The shell text screen does not deny it. A write stays
    // denied by the write list, which the shell trusted-root check applies.
    [Theory]
    [InlineData("netclaw.json")]
    [InlineData("tool-approvals.json")]
    [InlineData("hard-deny-overrides.json")]
    [InlineData("daemon.env")]
    [InlineData("devices.json")]
    [InlineData("bootstrap-state.json")]
    public void Config_file_is_readable_but_not_writable(string fileName)
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux),
            new SkillFeedsConfig());
        var configPath = Path.Combine(paths.ConfigDirectory, fileName);

        Assert.False(policy.FileSystem.IsProtected(configPath, PathOperation.Read));
        Assert.True(policy.FileSystem.IsProtected(configPath, PathOperation.Write));
        Assert.False(policy.CommandReferencesDeniedPath($"cat '{configPath}'"));
    }

    [Fact]
    public void Credentials_and_control_plane_files_remain_read_denied()
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux),
            new SkillFeedsConfig());
        string[] protectedPaths =
        [
            paths.SecretsPath,
            Path.Combine(paths.WebhooksDirectory, "github-issues.json"),
            Path.Combine(paths.KeysDirectory, "key-1.xml"),
            paths.SqliteDbPath,
            paths.PidFilePath,
            paths.LockFilePath,
            paths.RestartManifestPath
        ];

        Assert.All(protectedPaths, path => Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Read), path));
        // The shell text screen denies each of them, whatever the program.
        Assert.All(protectedPaths, path => Assert.True(policy.CommandReferencesDeniedPath($"cat '{path}'"), path));
    }

    // Program text can name the config directory in another spelling. The text
    // screen collapses "//", "/./", a trailing "/.", and "name/../" before it
    // matches the ".netclaw/config" marker, so each spelling stays denied.
    [Theory]
    [InlineData("jq -n 'import \"secrets\" as $s {search: \"~/.netclaw/./config\"}; $s'")]
    [InlineData("jq -n 'import \"secrets\" as $s {search: \"~/.netclaw//config\"}; $s'")]
    [InlineData("jq -n 'import \"secrets\" as $s {search: \"~/.netclaw/x/../config\"}; $s'")]
    [InlineData("jq -n 'import \"secrets\" as $s {search: \"$HOME/.netclaw/./config\"}; $s'")]
    [InlineData("python3 -c \"import os; print(os.listdir('/srv/.netclaw/config/.'))\"")]
    public void Program_text_that_spells_the_config_directory_stays_denied(string command)
    {
        var paths = new NetclawPaths("/home/user/.netclaw");
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux),
            new SkillFeedsConfig());

        Assert.True(policy.CommandReferencesDeniedPath(command), command);
        Assert.False(policy.CommandReferencesDeniedPath("jq -n 'import \"x\" as $s {search: \"~/.netclaw/./skills\"}; $s'"));
    }

    // The Netclaw home below the launch HOME, as in the default layout. The shell
    // screen denies each home form of a credential: "~user", "$HOME", "${HOME}",
    // and a "/./" segment. No file is read or written.
    [Theory]
    [InlineData("cat ~{user}/.netclaw/config/secrets.json")]
    [InlineData("cat \"$HOME\"/.netclaw/config/secrets.json")]
    [InlineData("cat ${HOME}/.netclaw/./config/secrets.json")]
    [InlineData("cat ~{user}/.netclaw/keys/key-1.xml")]
    [InlineData("cat \"$HOME\"/.netclaw/keys/key-1.xml")]
    [InlineData("cat ${HOME}/.netclaw/./keys/key-1.xml")]
    [InlineData("cat ~{user}/.netclaw/config/webhooks/route.json")]
    [InlineData("cat \"$HOME\"/.netclaw/config/webhooks/route.json")]
    [InlineData("cat ${HOME}/.netclaw/./config/webhooks/route.json")]
    public void Home_forms_of_a_credential_stay_denied(string template)
    {
        if (OperatingSystem.IsWindows())
            return;

        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var home = Assert.IsType<string>(environment.HomeDirectory);
        var policy = DaemonToolPathPolicyFactory.Create(new NetclawPaths(Path.Combine(home, ".netclaw")), environment, new SkillFeedsConfig());
        var command = template.Replace("{user}", Environment.UserName, StringComparison.Ordinal);

        Assert.True(policy.CommandReferencesDeniedPath(command), command);
    }

    [Theory]
    [InlineData(ShellPlatform.Linux)]
    [InlineData(ShellPlatform.MacOS)]
    [InlineData(ShellPlatform.Windows)]
    public void Skill_folders_are_writable_and_the_control_plane_is_not(ShellPlatform platform)
    {
        // Owner decision (2026-10-05): skills are agent guidance, not control plane.
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var environment = platform == ShellPlatform.Windows
            ? ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                PwshDialect.WindowsPowerShell51)
            : ShellExecutionEnvironment.CreateBash(platform);
        var policy = DaemonToolPathPolicyFactory.Create(paths, environment, new SkillFeedsConfig());
        string[] skillPaths =
        [
            Path.Combine(paths.SystemSkillsDirectory, "netclaw-operations", "SKILL.md"),
            Path.Combine(paths.ServerFeedDirectory("team"), "disk-cleanup", "scripts", "audit.sh"),
        ];
        string[] controlPlanePaths =
        [
            Path.Combine(paths.ConfigDirectory, "netclaw.json"),
            Path.Combine(paths.ConfigDirectory, "tool-approvals.json"),
            paths.SecretsPath,
            Path.Combine(paths.WebhooksDirectory, "github-issues.json"),
            Path.Combine(paths.KeysDirectory, "key-1.xml"),
            paths.SqliteDbPath,
            paths.SqliteDbPath + "-wal",
            paths.PidFilePath,
            paths.LockFilePath,
            paths.RestartManifestPath,
            Path.Combine(paths.ToolingShadowDirectory, "tool-index.md"),
        ];

        Assert.All(skillPaths, path => Assert.False(policy.FileSystem.IsProtected(path, PathOperation.Write), path));
        Assert.All(skillPaths, path => Assert.False(policy.FileSystem.IsProtected(path, PathOperation.Read), path));
        Assert.All(controlPlanePaths, path => Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Write), path));
    }

    // Owner decision (2026-10-07): ~/.ssh and ~/.aws are denied like the control
    // plane. A file tool and a shell path operand meet the same lists. The
    // program that needs them (ssh, git, aws) reads them as the child process.
    private static Fixture CreateHomePolicy()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));
        var home = Assert.IsType<string>(environment.HomeDirectory);
        var policy = DaemonToolPathPolicyFactory.Create(
            new NetclawPaths(Path.Combine(home, ".netclaw")),
            environment,
            new SkillFeedsConfig());
        return new Fixture(home, policy);
    }

    private sealed record Fixture(string Home, ToolPathPolicy Policy);

    [SlopwatchSuppress("SW001", "The shell forms need a Bash home on a POSIX host.")]
    [Theory(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsPosix),
        Skip = "The shell forms need a Bash home")]
    [InlineData(".ssh/id_ed25519")]
    [InlineData(".ssh/id_ed25519.pub")]
    [InlineData(".ssh")]
    [InlineData(".aws/credentials")]
    [InlineData(".aws")]
    [InlineData(".gnupg/pubring.kbx")]
    [InlineData(".kube/config")]
    [InlineData(".kube")]
    [InlineData(".azure/accessTokens.json")]
    [InlineData(".config/gh/hosts.yml")]
    [InlineData(".config/gcloud/credentials.db")]
    [InlineData(".docker/config.json")]
    [InlineData(".netrc")]
    [InlineData(".git-credentials")]
    public void Credential_locations_are_denied_to_read_write_and_shell(string relativePath)
    {
        var (home, policy) = CreateHomePolicy();
        var path = Path.Combine(home, relativePath);

        Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Read), path);
        Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Write), path);
        Assert.True(policy.CommandReferencesDeniedPath($"cat '{path}'"), path);
        Assert.True(policy.CommandReferencesDeniedPath($"cat ~/{relativePath}"), relativePath);
        Assert.True(policy.CommandReferencesDeniedPath($"cat \"$HOME\"/{relativePath}"), relativePath);
        Assert.True(policy.CommandReferencesDeniedPath($"cat ${{HOME}}/{relativePath}"), relativePath);
    }

    [SlopwatchSuppress("SW001", "The shell forms need a Bash home on a POSIX host.")]
    [Fact(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsPosix),
        Skip = "The shell forms need a Bash home")]
    public void Resolved_spellings_of_a_credential_directory_are_denied()
    {
        var (home, policy) = CreateHomePolicy();

        Assert.True(policy.CommandReferencesDeniedPath("cat id_ed25519", Path.Combine(home, ".ssh")));
        Assert.True(policy.CommandReferencesDeniedPath("cat ~/.s*/id_ed25519"));
        Assert.True(policy.CommandReferencesDeniedPath("cd ~ && cat .ssh/id_ed25519"));
        Assert.True(policy.CommandReferencesDeniedPath("kubectl --kubeconfig ~/.kube/config get pods"));
        Assert.True(policy.CommandReferencesDeniedPath("gpg --homedir ~/.gnupg --list-keys"));
        Assert.True(policy.CommandReferencesDeniedPath("cd ~/.kube && cat config"));
    }

    // Workspace paths and patterns that only spell the directory name stay open,
    // as on dev. The text indicators are the home-anchored spellings only.
    [SlopwatchSuppress("SW001", "The shell forms need a Bash home on a POSIX host.")]
    [Theory(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsPosix),
        Skip = "The shell forms need a Bash home")]
    [InlineData("cat ~/projects/app/.aws/config")]
    [InlineData("cat infra/.ssh/config")]
    [InlineData("cat .devcontainer/.aws/config")]
    [InlineData("cd app && cat .aws/config")]
    [InlineData("git diff -- infra/.ssh/config")]
    [InlineData("grep -rn '\\.ssh' .")]
    [InlineData("sed 's/\\.ssh//' f")]
    [InlineData("grep -c 'docs\\.aws\\.amazon\\.com' README.md")]
    [InlineData("grep -rn \"/.aws\" .")]
    [InlineData("git log --grep=\"/.ssh\"")]
    [InlineData("git commit -m \"docs: explain the /.aws mount\"")]
    [InlineData("curl https://raw.githubusercontent.com/x/dotfiles/main/.ssh/config")]
    [InlineData("curl https://docs.aws.amazon.com/cli/latest/userguide/")]
    [InlineData("git clone https://github.com/example/dotfiles.ssh.git")]
    [InlineData("cat notes.ssh.md")]
    [InlineData("cat ~/.sshrc")]
    [InlineData("cat ~/.awsome/x")]
    [InlineData("cat ~/.netrc-example.md")]
    [InlineData("cat ~/.docker/contexts/meta.json")]
    [InlineData("cat ~/.config/git/config")]
    [InlineData("cat tsconfig.json")]
    [InlineData("cat infra/.kube/config")]
    [InlineData("git push origin main")]
    [InlineData("gh pr list")]
    [InlineData("kubectl get pods")]
    [InlineData("docker pull alpine")]
    [InlineData("gcloud auth list")]
    [InlineData("az account show")]
    [InlineData("gpg --sign file")]
    [InlineData("curl -n https://example.com/x")]
    public void A_workspace_path_or_pattern_that_spells_a_credential_directory_stays_open(string command)
    {
        var (home, policy) = CreateHomePolicy();

        Assert.False(policy.CommandReferencesDeniedPath(command), command);
        Assert.False(policy.CommandReferencesDeniedPath(command, Path.Combine(home, "projects")), command);
    }

    [SlopwatchSuppress("SW001", "The shell forms need a Bash home on a POSIX host.")]
    [Theory(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsPosix),
        Skip = "The shell forms need a Bash home")]
    [InlineData("docker run -v $HOME/.aws:/root/.aws:ro alpine")]
    [InlineData("docker run -v ${HOME}/.ssh:/root/.ssh alpine")]
    [InlineData("scp ~/.ssh/id_ed25519.pub host:")]
    public void A_command_that_hands_the_home_directory_to_a_child_is_denied(string command)
    {
        var (_, policy) = CreateHomePolicy();

        Assert.True(policy.CommandReferencesDeniedPath(command), command);
    }

    // Quoted text that spells a protected path is screened as the control plane
    // text is. The credential directories are no stricter than the control plane.
    [SlopwatchSuppress("SW001", "The shell forms need a Bash home on a POSIX host.")]
    [Fact(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsPosix),
        Skip = "The shell forms need a Bash home")]
    public void Quoted_text_that_spells_a_credential_directory_is_screened_as_control_plane_text_is()
    {
        var (_, policy) = CreateHomePolicy();

        Assert.True(policy.CommandReferencesDeniedPath("echo \"see ~/.netclaw/config/secrets.json\""));
        Assert.True(policy.CommandReferencesDeniedPath("echo \"see ~/.ssh/config for details\""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/home")]
    public void A_home_that_is_not_fully_qualified_adds_no_credential_entry(string? home)
    {
        Assert.Empty(DaemonToolPathPolicyFactory.CredentialLocations(home));
    }

    private static string Root(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-policy-" + name));

    [Fact]
    public void A_fully_qualified_home_adds_the_credential_table()
    {
        var home = Root("home");

        Assert.Equal(
            new[]
            {
                ".ssh", ".aws", ".gnupg", ".kube", ".azure", ".netrc", ".git-credentials",
                Path.Combine(".docker", "config.json"),
                Path.Combine(".config", "gh"),
                Path.Combine(".config", "gcloud"),
            }.Select(relative => Path.Combine(home, relative)),
            DaemonToolPathPolicyFactory.CredentialLocations(home));
    }

    [Fact]
    public void Xdg_config_home_adds_its_two_entries_only_when_it_differs()
    {
        var home = Root("home");
        var xdg = Root("xdg");

        var same = DaemonToolPathPolicyFactory.CredentialLocations(home, Path.Combine(home, ".config"));
        var other = DaemonToolPathPolicyFactory.CredentialLocations(home, xdg);

        Assert.Equal(DaemonToolPathPolicyFactory.CredentialLocations(home).Length, same.Length);
        Assert.Equal(same.Length + 2, other.Length);
        Assert.Contains(Path.Combine(xdg, "gh"), other);
        Assert.Contains(Path.Combine(xdg, "gcloud"), other);
        Assert.Empty(DaemonToolPathPolicyFactory.CredentialLocations(null, "relative/xdg"));
    }

    [Fact]
    public void Windows_adds_its_own_locations_and_skips_a_base_that_is_not_fully_qualified()
    {
        var home = Root("home");
        var appData = Root("appdata");
        var programData = Root("programdata");

        var windows = DaemonToolPathPolicyFactory.CredentialLocations(home, null, windows: true, appData, programData);
        var other = DaemonToolPathPolicyFactory.CredentialLocations(home, null, windows: false, appData, programData);
        var withoutBases = DaemonToolPathPolicyFactory.CredentialLocations(home, null, windows: true, "", "relative");

        Assert.Contains(Path.Combine(home, "_netrc"), windows);
        Assert.Contains(Path.Combine(appData, "gcloud"), windows);
        Assert.Contains(Path.Combine(appData, "GitHub CLI"), windows);
        Assert.Contains(Path.Combine(appData, "gnupg"), windows);
        Assert.Contains(Path.Combine(programData, "ssh"), windows);
        Assert.DoesNotContain(Path.Combine(home, "_netrc"), other);
        Assert.DoesNotContain(Path.Combine(programData, "ssh"), other);
        Assert.Equal(other.Length + 1, withoutBases.Length);
    }

    // Windows only: the production factory with the real profile folder and the
    // PowerShell host. The decisions need no file on disk.
    private static (string Home, ToolPathPolicy Policy) CreateWindowsPolicy()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            PwshDialect.WindowsPowerShell51);
        var policy = DaemonToolPathPolicyFactory.Create(
            new NetclawPaths(Path.Combine(home, ".netclaw")),
            environment,
            new SkillFeedsConfig());
        return (home, policy);
    }

    [SlopwatchSuppress("SW001", "The PowerShell host and the profile folder exist only on Windows.")]
    [Theory(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsWindows),
        Skip = "The PowerShell host and the profile folder exist only on Windows")]
    [InlineData(@"Get-Content ~\.ssh\id_ed25519")]
    [InlineData(@"gc $HOME\.ssh\id_ed25519")]
    [InlineData(@"gc $HOME/.ssh/id_ed25519")]
    [InlineData(@"cat $env:USERPROFILE\.aws\credentials")]
    [InlineData(@"cat ${env:USERPROFILE}\.aws\credentials")]
    [InlineData(@"type %USERPROFILE%\.ssh\id_ed25519")]
    [InlineData(@"gc $env:USERPROFILE\.kube\config")]
    [InlineData(@"gc $env:USERPROFILE\.docker\config.json")]
    [InlineData(@"gc $env:USERPROFILE\_netrc")]
    [InlineData(@"gc $env:USERPROFILE\.netrc")]
    [InlineData(@"gc $env:USERPROFILE\.git-credentials")]
    [InlineData(@"gc $env:APPDATA\gcloud\credentials.db")]
    [InlineData(@"gc '$env:APPDATA\GitHub CLI\hosts.yml'")]
    [InlineData(@"gc $env:PROGRAMDATA\ssh\administrators_authorized_keys")]
    [InlineData(@"gc $env:USERPROFILE\.SSH\id_ed25519")]
    public void Windows_powershell_spellings_of_a_credential_location_are_denied(string command)
    {
        var (_, policy) = CreateWindowsPolicy();

        Assert.True(policy.CommandReferencesDeniedPath(command), command);
    }

    [SlopwatchSuppress("SW001", "The PowerShell host and the profile folder exist only on Windows.")]
    [Theory(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsWindows),
        Skip = "The PowerShell host and the profile folder exist only on Windows")]
    [InlineData(@"gc infra\.ssh\config")]
    [InlineData(@"gc .devcontainer\.aws\config")]
    [InlineData(@"gc $env:USERPROFILE\projects\app\.aws\config")]
    [InlineData(@"Select-String '\.ssh' *")]
    [InlineData(@"Select-String 'docs\.aws\.amazon\.com' README.md")]
    [InlineData(@"git push origin main")]
    [InlineData(@"gh pr list")]
    [InlineData(@"kubectl get pods")]
    [InlineData(@"gc $env:USERPROFILE\.sshrc")]
    public void Windows_workspace_paths_and_unrelated_commands_stay_open(string command)
    {
        var (_, policy) = CreateWindowsPolicy();

        Assert.False(policy.CommandReferencesDeniedPath(command), command);
    }

    [SlopwatchSuppress("SW001", "The profile folder and the AppData folders exist only on Windows.")]
    [Theory(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsWindows),
        Skip = "The profile folder and the AppData folders exist only on Windows")]
    [InlineData(@".ssh\id_ed25519", false)]
    [InlineData(@".ssh/id_ed25519", false)]
    [InlineData(@".SSH\ID_ED25519", false)]
    [InlineData(@".aws\credentials", false)]
    [InlineData(@".kube\config", false)]
    [InlineData(@".docker\config.json", false)]
    [InlineData(@"_netrc", false)]
    [InlineData(@"%APPDATA%\gcloud\credentials.db", false)]
    [InlineData(@"%APPDATA%\GitHub CLI\hosts.yml", false)]
    [InlineData(@"%APPDATA%\gnupg\pubring.kbx", false)]
    [InlineData(@"infra\.ssh\config", true)]
    [InlineData(@".devcontainer\.aws\config", true)]
    [InlineData(@".docker\contexts\meta.json", true)]
    public void Windows_file_tools_meet_the_credential_table_with_either_slash_and_any_case(string relative, bool open)
    {
        var (home, policy) = CreateWindowsPolicy();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = relative.StartsWith("%APPDATA%", StringComparison.Ordinal)
            ? Path.Combine(appData, relative["%APPDATA%\\".Length..])
            : Path.Combine(home, relative);

        foreach (var spelling in new[] { path, path.Replace('\\', '/'), path.ToUpperInvariant(), path.ToLowerInvariant() })
        {
            Assert.Equal(!open, policy.FileSystem.IsProtected(spelling, PathOperation.Read));
            Assert.Equal(!open, policy.FileSystem.IsProtected(spelling, PathOperation.Write));
        }
    }

    [SlopwatchSuppress("SW001", "The ProgramData folder exists only on Windows.")]
    [Fact(SkipType = typeof(TestPlatform), SkipUnless = nameof(TestPlatform.IsWindows),
        Skip = "The ProgramData folder exists only on Windows")]
    public void Windows_system_ssh_folder_is_denied()
    {
        var (_, policy) = CreateWindowsPolicy();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "administrators_authorized_keys");

        Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Write));
        Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Read));
    }

    [Theory]
    [InlineData("tool-index.md")]
    [InlineData("mcp/synthetic-server.md")]
    public void Operator_tool_catalogs_are_denied_to_read_write_and_shell(string relativePath)
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux),
            new SkillFeedsConfig());
        var catalogPath = Path.Combine([paths.ToolingShadowDirectory, .. relativePath.Split('/')]);

        Assert.True(policy.FileSystem.IsProtected(catalogPath, PathOperation.Write));
        Assert.True(policy.FileSystem.IsProtected(catalogPath, PathOperation.Read));
        Assert.True(policy.CommandReferencesDeniedPath($"inspect '{catalogPath}'"));
        Assert.True(policy.CommandReferencesDeniedPath("find", catalogPath));
    }
}
