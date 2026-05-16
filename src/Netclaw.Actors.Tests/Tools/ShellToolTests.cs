// -----------------------------------------------------------------------
// <copyright file="ShellToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class ShellToolTests
{
    private readonly ShellTool _tool = new(new ToolConfig());

    [Fact]
    public async Task Execute_echo_returns_output()
    {
        var args = ToolInput.Create("Command", "echo hello");
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("hello", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Execute_captures_stderr()
    {
        var args = ToolInput.Create("Command", "echo error >&2");
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("error", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Execute_returns_nonzero_exit_code()
    {
        var args = ToolInput.Create("Command", "exit 42");
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Exit code: 42", result);
    }

    [Fact]
    public async Task Timeout_kills_long_running_process()
    {
        var tool = new ShellTool(new ToolConfig { ShellTimeoutSeconds = 1 });
        var args = ToolInput.Create("Command", "sleep 100");

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("timed out", result);
    }

    [Fact]
    public async Task Requested_timeout_overrides_default_timeout()
    {
        var tool = new ShellTool(new ToolConfig { ShellTimeoutSeconds = 1 });
        var args = ToolInput.Create("Command", "sleep 2");
        var context = new ToolExecutionContext("test/thread", Path.GetTempPath())
        {
            Audience = TrustAudience.Personal,
            RequestedTimeoutSeconds = 3
        };

        var result = await tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Caller_cancellation_returns_gracefully()
    {
        // Reproduces the session-pipeline path: ShellTool's own timeout is long,
        // and cancellation instead arrives via the *outer* ct (the pipeline's
        // per-tool deadline). ShellTool must catch that, kill the process, and
        // return a message rather than letting the cancellation escape as an
        // exception. If the kill failed, draining the pipes would hang and this
        // test would never complete — so a passing run also proves the process
        // was terminated.
        var tool = new ShellTool(new ToolConfig { ShellTimeoutSeconds = 100 });
        var args = ToolInput.Create("Command", "sleep 120");
        var context = new ToolExecutionContext("test/thread", Path.GetTempPath())
        {
            Audience = TrustAudience.Personal,
            RequestedTimeoutSeconds = 100
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var result = await tool.ExecuteAsync(args, context, cts.Token);

        Assert.Contains("cancelled", result);
    }

    [Fact]
    public async Task Output_truncation_applies()
    {
        var tool = new ShellTool(new ToolConfig { MaxOutputChars = 50 });
        // Generate output longer than 50 chars — use cross-platform command
        var command = OperatingSystem.IsWindows()
            ? "python -c \"print('x' * 200)\""
            : "printf 'x%.0s' {1..200}";
        var args = ToolInput.Create("Command", command);

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("[output truncated]", result);
    }

    [Fact]
    public async Task Working_directory_is_respected()
    {
        var tmpDir = Path.GetTempPath();
        // Use platform-appropriate command to print working directory
        var command = OperatingSystem.IsWindows() ? "cd" : "pwd";
        var args = ToolInput.Create("Command", command, "WorkingDirectory", tmpDir);

        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        // Normalize paths for comparison: resolve symlinks, trim trailing separators
        var resolvedTmpDir = Path.GetFullPath(tmpDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Contains("Exit code: 0", result);
        Assert.Contains(resolvedTmpDir, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_command_returns_error()
    {
        var args = ToolInput.Empty();
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Command", result);
        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── Cwd resolution chain: explicit arg → ProjectDirectory → SessionDirectory ──

    [Fact]
    public async Task Cwd_falls_back_to_project_directory_when_no_explicit_arg()
    {
        var projectDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var sessionDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(sessionDir);
        try
        {
            var context = new ToolExecutionContext("session-1", sessionDir) { Audience = TrustAudience.Personal, ProjectDirectory = projectDir };
            var args = ToolInput.Create("Command", OperatingSystem.IsWindows() ? "cd" : "pwd");

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.Contains("Exit code: 0", result);
            var resolved = projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.Contains(resolved, result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, recursive: true);
            if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Cwd_falls_back_to_session_directory_when_project_directory_null()
    {
        var sessionDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(sessionDir);
        try
        {
            var context = new ToolExecutionContext("session-1", sessionDir) { Audience = TrustAudience.Personal };
            // ProjectDirectory not set
            var args = ToolInput.Create("Command", OperatingSystem.IsWindows() ? "cd" : "pwd");

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.Contains("Exit code: 0", result);
            var resolved = sessionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.Contains(resolved, result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Cwd_explicit_arg_overrides_project_and_session_directories()
    {
        var explicitDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var projectDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var sessionDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(explicitDir);
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(sessionDir);
        try
        {
            var context = new ToolExecutionContext("session-1", sessionDir) { Audience = TrustAudience.Personal, ProjectDirectory = projectDir };
            var args = ToolInput.Create(
                "Command", OperatingSystem.IsWindows() ? "cd" : "pwd",
                "WorkingDirectory", explicitDir);

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.Contains("Exit code: 0", result);
            var resolved = explicitDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.Contains(resolved, result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(explicitDir)) Directory.Delete(explicitDir, recursive: true);
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, recursive: true);
            if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Cwd_does_not_inherit_daemon_process_directory()
    {
        var sessionDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(sessionDir);
        try
        {
            // The daemon's cwd is wherever this test process is running. We
            // assert the resolved cwd is the session dir, not whatever
            // Environment.CurrentDirectory happens to be — proving the
            // ProcessStartInfo default-fall-through is gone.
            var context = new ToolExecutionContext("session-1", sessionDir) { Audience = TrustAudience.Personal };
            var args = ToolInput.Create("Command", OperatingSystem.IsWindows() ? "cd" : "pwd");

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.Contains("Exit code: 0", result);
            var sessionResolved = sessionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.Contains(sessionResolved, result, StringComparison.OrdinalIgnoreCase);

            var daemonCwd = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(daemonCwd, sessionResolved, StringComparison.OrdinalIgnoreCase))
            {
                // Only assert non-inheritance when the daemon cwd is distinct
                // from the session dir; otherwise the test is vacuous.
                Assert.DoesNotContain($"\n{daemonCwd}\n", result);
            }
        }
        finally
        {
            if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Null_arguments_returns_error()
    {
        var result = await _tool.ExecuteAsync(null, CancellationToken.None);
        Assert.Contains("No arguments provided", result);
    }

    [Fact]
    public void TruncateOutput_no_truncation_when_under_limit()
    {
        var input = "short output";
        Assert.Equal(input, ShellTool.TruncateOutput(input, 100));
    }

    [Fact]
    public void TruncateOutput_truncates_and_appends_indicator()
    {
        var input = new string('x', 200);
        var result = ShellTool.TruncateOutput(input, 50);

        Assert.StartsWith(new string('x', 50), result);
        Assert.EndsWith("[output truncated]", result);
    }

    [Fact]
    public async Task Command_referencing_denied_path_returns_access_denied()
    {
        var secretsPath = "/home/user/.netclaw/config/secrets.json";
        var policy = new ToolPathPolicy([secretsPath]);
        var tool = new ShellTool(new ToolConfig(), policy);

        var args = ToolInput.Create("Command", $"cat {secretsPath}");

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("protected file path", result);
        Assert.Contains("Access denied", result);
    }

    [Fact]
    public async Task Execute_redacts_secret_like_output()
    {
        var args = ToolInput.Create("Command", "echo API_KEY=secret123");

        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("API_KEY=***REDACTED***", result);
        Assert.DoesNotContain("secret123", result);
    }

    [Fact]
    public async Task High_risk_glob_on_netclaw_config_is_blocked()
    {
        var secretsPath = "/home/user/.netclaw/config/secrets.json";
        var policy = new ToolPathPolicy([secretsPath]);
        var tool = new ShellTool(new ToolConfig(), policy);

        var args = ToolInput.Create("Command", "cat ~/.netclaw/config/*.json");

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("protected file path", result);
        Assert.Contains("Access denied", result);
    }

    [Fact]
    public async Task Hard_deny_blocks_daemon_stop()
    {
        var commandPolicy = new ShellCommandPolicy();
        var tool = new ShellTool(new ToolConfig(), commandPolicy: commandPolicy);

        var args = ToolInput.Create("Command", "netclaw daemon stop");
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("hard deny policy", result);
    }

    [Fact]
    public async Task Hard_deny_blocks_kill_command()
    {
        var commandPolicy = new ShellCommandPolicy();
        var tool = new ShellTool(new ToolConfig(), commandPolicy: commandPolicy);

        var args = ToolInput.Create("Command", "kill -9 12345");
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("hard deny policy", result);
    }

    [Fact]
    public async Task Hard_deny_checked_before_path_policy()
    {
        var commandPolicy = new ShellCommandPolicy();
        var pathPolicy = new ToolPathPolicy(["/some/path"]);
        var tool = new ShellTool(new ToolConfig(), pathPolicy, commandPolicy);

        var args = ToolInput.Create("Command", "netclaw daemon stop");
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        // Should hit hard deny, not path policy
        Assert.Contains("hard deny policy", result);
        Assert.DoesNotContain("protected file path", result);
    }

    [Fact]
    public async Task Path_policy_still_blocks_sensitive_paths_when_command_is_not_hard_denied()
    {
        var commandPolicy = new ShellCommandPolicy();
        var pathPolicy = new ToolPathPolicy(["/home/user/.netclaw/config/secrets.json"]);
        var tool = new ShellTool(new ToolConfig(), pathPolicy, commandPolicy);

        var args = ToolInput.Create("Command", "cat /home/user/.netclaw/config/secrets.json");

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.DoesNotContain("hard deny policy", result);
        Assert.Contains("protected file path", result);
        Assert.Contains("Access denied", result);
    }
}
