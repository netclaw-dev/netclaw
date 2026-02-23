using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class ShellToolTests
{
    private readonly ShellTool _tool = new(new ToolConfig());

    [Fact]
    public async Task Execute_echo_returns_output()
    {
        var args = new Dictionary<string, object?> { ["Command"] = "echo hello" };
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("hello", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Execute_captures_stderr()
    {
        var args = new Dictionary<string, object?> { ["Command"] = "echo error >&2" };
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("error", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Execute_returns_nonzero_exit_code()
    {
        var args = new Dictionary<string, object?> { ["Command"] = "exit 42" };
        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("Exit code: 42", result);
    }

    [Fact]
    public async Task Timeout_kills_long_running_process()
    {
        var tool = new ShellTool(new ToolConfig { ShellTimeoutSeconds = 1 });
        var args = new Dictionary<string, object?> { ["Command"] = "sleep 100" };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("timed out", result);
    }

    [Fact]
    public async Task Output_truncation_applies()
    {
        var tool = new ShellTool(new ToolConfig { MaxOutputChars = 50 });
        // Generate output longer than 50 chars — use cross-platform command
        var command = OperatingSystem.IsWindows()
            ? "python -c \"print('x' * 200)\""
            : "printf 'x%.0s' {1..200}";
        var args = new Dictionary<string, object?> { ["Command"] = command };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("[output truncated]", result);
    }

    [Fact]
    public async Task Working_directory_is_respected()
    {
        var tmpDir = Path.GetTempPath();
        // Use platform-appropriate command to print working directory
        var command = OperatingSystem.IsWindows() ? "cd" : "pwd";
        var args = new Dictionary<string, object?>
        {
            ["Command"] = command,
            ["WorkingDirectory"] = tmpDir
        };

        var result = await _tool.ExecuteAsync(args, CancellationToken.None);

        // Normalize paths for comparison: resolve symlinks, trim trailing separators
        var resolvedTmpDir = Path.GetFullPath(tmpDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Contains("Exit code: 0", result);
        Assert.Contains(resolvedTmpDir, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_command_returns_error()
    {
        var args = new Dictionary<string, object?>();
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
}
