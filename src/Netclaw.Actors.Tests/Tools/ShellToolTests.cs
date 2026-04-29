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
            RequestedTimeoutSeconds = 3
        };

        var result = await tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("Exit code: 0", result);
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
