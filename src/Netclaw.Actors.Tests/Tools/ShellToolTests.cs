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
    private static readonly ShellExecutionEnvironment ShellEnvironment = TestShellEnvironment.Current;
    private readonly ShellTool _tool = CreateTool();

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [Fact]
    public void Constructor_preserves_three_parameter_binary_signature()
    {
        var constructor = typeof(ShellTool).GetConstructor(
            [typeof(ToolConfig), typeof(ToolPathPolicy), typeof(ShellCommandPolicy)]);

        Assert.NotNull(constructor);
    }

    private static ShellTool CreateTool(ToolConfig? config = null)
    {
        var commandPolicy = new ShellCommandPolicy(ShellEnvironment);
        return new ShellTool(
            config ?? new ToolConfig(),
            new ToolPathPolicy(ShellEnvironment, []),
            commandPolicy);
    }

    [Fact]
    public void Constructor_rejects_policies_from_different_shell_environments()
    {
        var first = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var second = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);

        var exception = Assert.Throws<ArgumentException>(() => new ShellTool(
            new ToolConfig(),
            new ToolPathPolicy(first, []),
            new ShellCommandPolicy(second)));

        Assert.Contains("same shell environment", exception.Message);
    }

    [Fact]
    public void Shell_schema_prefers_file_tools_and_typed_working_directory()
    {
        Assert.Contains("shell semantics", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("local search, VCS, builds, tests, processes", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("declared-project work, omit WorkingDirectory", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("session_dir only for disposable non-project work", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("smallest operation that answers the request", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("Do not use shell only to verify successful structured results", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("After approval-required results, do not retry or substitute variants", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("Treat 'Tool access denied:' as terminal; do not change scope", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("Apply one 'Tool execution deferred:' correction unchanged", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("Do not use shell for known file reads", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("or disposable text unless shell behavior is requested", _tool.Description, StringComparison.Ordinal);

        var commandDescription = _tool.ParameterSchema
            .GetProperty("properties")
            .GetProperty("Command")
            .GetProperty("description")
            .GetString();
        var description = _tool.ParameterSchema
            .GetProperty("properties")
            .GetProperty("WorkingDirectory")
            .GetProperty("description")
            .GetString();

        Assert.Contains("smallest shell operation that answers the request", commandDescription, StringComparison.Ordinal);
        Assert.Contains("Do not verify successful structured results with shell", commandDescription, StringComparison.Ordinal);
        Assert.Contains("Do not retry approval-required variants", commandDescription, StringComparison.Ordinal);
        Assert.Contains("Treat 'Tool access denied:' as terminal; do not change scope", commandDescription, StringComparison.Ordinal);
        Assert.Contains("Apply one 'Tool execution deferred:' correction unchanged", commandDescription, StringComparison.Ordinal);
        Assert.Contains("Do not use shell for disposable text unless shell behavior is requested", commandDescription, StringComparison.Ordinal);
        Assert.Contains("Set only for one call", description, StringComparison.Ordinal);
        Assert.Contains("named child directory or worktree", description, StringComparison.Ordinal);
        Assert.Contains("Omit for declared-project work", description, StringComparison.Ordinal);
        Assert.Contains("session_dir only for disposable non-project work", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(FileWriteTool), "successful result confirms the write")]
    [InlineData(typeof(FileEditTool), "successful result confirms the change")]
    public void File_mutation_schema_does_not_request_shell_verification(Type toolType, string expectedResult)
    {
        var attribute = Assert.Single(
            toolType.GetCustomAttributes(typeof(NetclawToolAttribute), inherit: false)
                .Cast<NetclawToolAttribute>());

        Assert.Contains(expectedResult, attribute.Description, StringComparison.Ordinal);
        Assert.Contains("do not verify it with shell unless requested", attribute.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void File_schemas_route_disposable_text_without_shell()
    {
        var writeAttribute = Assert.Single(
            typeof(FileWriteTool).GetCustomAttributes(typeof(NetclawToolAttribute), inherit: false)
                .Cast<NetclawToolAttribute>());
        var readAttribute = Assert.Single(
            typeof(FileReadTool).GetCustomAttributes(typeof(NetclawToolAttribute), inherit: false)
                .Cast<NetclawToolAttribute>());

        Assert.Contains("disposable session text", writeAttribute.Description, StringComparison.Ordinal);
        Assert.Contains("when shell behavior is not requested", writeAttribute.Description, StringComparison.Ordinal);
        Assert.Contains("read disposable text after file_write", readAttribute.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(FileReadTool), "known local file read")]
    [InlineData(typeof(FileListTool), "known local directory listing")]
    [InlineData(typeof(FileWriteTool), "known local file")]
    [InlineData(typeof(FileEditTool), "known local file")]
    [InlineData(typeof(WebSearchTool), "external discovery")]
    [InlineData(typeof(WebFetchTool), "known external page or URL")]
    public void First_party_tool_schema_states_its_preferred_task(
        Type toolType,
        string expectedTask)
    {
        var attribute = Assert.Single(
            toolType.GetCustomAttributes(typeof(NetclawToolAttribute), inherit: false)
                .Cast<NetclawToolAttribute>());

        Assert.Contains(expectedTask, attribute.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_selected_executable_fails_without_fallback()
    {
        const string missingExecutable = @"C:\missing\pwsh.exe";
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            missingExecutable,
            ShellSyntaxTree.PwshDialect.PowerShell7);
        var tool = new ShellTool(
            new ToolConfig(),
            new ToolPathPolicy(environment, []),
            new ShellCommandPolicy(environment));

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Command", "Get-ChildItem"),
            TestToolExecutionContext.CreateUnbound(),
            TestContext.Current.CancellationToken);

        Assert.Contains(missingExecutable, result);
        Assert.DoesNotContain("powershell.exe", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_echo_returns_output()
    {
        var args = ToolInput.Create("Command", "echo hello");
        var result = await _tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Contains("hello", result);
        Assert.Contains("Exit code: 0", result);
        // A normal command reaches EOF cleanly. The result must not carry a
        // grace-cut marker that tells the agent the capture is incomplete.
        Assert.DoesNotContain("background process", result);
    }

    [SlopwatchSuppress("SW001", "This native fallback test requires Windows PowerShell 5.1.")]
    [Fact(SkipUnless = nameof(IsWindows), Skip = "Native Windows PowerShell 5.1 execution requires Windows.")]
    public async Task Windows_power_shell_51_executes_through_the_selected_host()
    {
        var environment = TestShellEnvironment.CreateWindowsPowerShell51();
        var tool = new ShellTool(
            new ToolConfig(),
            new ToolPathPolicy(environment, []),
            new ShellCommandPolicy(environment));

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Command", "Write-Output windows-powershell-51"),
            TestToolExecutionContext.CreateUnbound(),
            TestContext.Current.CancellationToken);

        Assert.Contains("windows-powershell-51", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Execute_captures_stderr()
    {
        var args = ToolInput.Create("Command", TestShellEnvironment.StandardErrorCommand);
        var result = await _tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Contains("error", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Execute_returns_nonzero_exit_code()
    {
        var args = ToolInput.Create("Command", "exit 42");
        var result = await _tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Contains("Exit code: 42", result);
    }

    [Fact]
    public async Task Timeout_kills_long_running_process()
    {
        var tool = CreateTool();
        var args = ToolInput.Create("Command", TestShellEnvironment.LongRunningCommand);
        var context = TestToolExecutionContext.CreateBound("test/thread", Path.GetTempPath(), new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ExecutionTimeout = new ToolExecutionTimeout(TimeSpan.FromSeconds(1))
        });

        var result = await tool.ExecuteAsync(args, context, CancellationToken.None);

        Assert.Contains("timed out", result);
    }

    [SlopwatchSuppress("SW001", "Reproduces a backgrounded child holding the pipe open; the case needs POSIX `&` semantics.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "Requires POSIX background-job (`&`) semantics.")]
    public async Task Direct_process_exit_with_backgrounded_child_holding_pipe_open_returns_promptly()
    {
        // The direct bash process exits at once. The backgrounded sleep
        // inherits stdout/stderr and holds the pipe write end open for its
        // own life span — the same shape as a self-daemonizing process, for
        // example nginx. The tool must return once bash exits. It must not
        // wait for the still-running child.
        var tool = CreateTool();
        var args = ToolInput.Create("Command", "sleep 20 & exit 0");
        var context = TestToolExecutionContext.CreateBound("test/thread", Path.GetTempPath(), new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ExecutionTimeout = new ToolExecutionTimeout(TimeSpan.FromSeconds(90))
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await tool.ExecuteAsync(args, context, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Contains("Exit code: 0", result);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"The tool must return soon after the direct process exits. It took {stopwatch.Elapsed}.");

        // The grace window cut the drain before EOF. The backgrounded sleep
        // process still holds the pipe open. The result must show this cut,
        // not a capture that looks complete.
        Assert.Contains("background process", result);
    }

    [Fact]
    public async Task Caller_cancellation_kills_child_process_tree_and_returns_gracefully()
    {
        // Reproduces the session-pipeline path: ShellTool's own timeout is long,
        // and cancellation instead arrives via the *outer* ct (the pipeline's
        // per-tool deadline). ShellTool must catch that, kill the process, and
        // return a message rather than letting the cancellation escape as an
        // exception. On Unix the command also spawns a background child that
        // inherits stdout/stderr; if the tree kill regresses, that child keeps
        // the pipe write-ends open and the test never completes.
        var tool = CreateTool();
        var command = ShellEnvironment.Grammar == ShellGrammar.PowerShell
            ? "ping.exe 127.0.0.1 -n 120 | Out-Null"
            : "sleep 120 & wait";
        var args = ToolInput.Create("Command", command);
        var context = TestToolExecutionContext.CreateBound("test/thread", Path.GetTempPath(), new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ExecutionTimeout = new ToolExecutionTimeout(TimeSpan.FromSeconds(100))
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var result = await tool.ExecuteAsync(args, context, cts.Token);

        Assert.Contains("cancelled", result);
    }

    [Fact]
    public async Task ShellTool_returns_raw_combined_output_without_spilling()
    {
        // ShellTool only returns its (bounded) raw output now — redaction and the
        // inline-budget bound + spill happen centrally in DispatchingToolExecutor
        // (covered by DispatchingToolExecutorTests). `echo` is available in both
        // canonical host grammars; a long literal is deterministic on stdout.
        var tool = CreateTool();
        var args = ToolInput.Create("Command", $"echo {new string('x', 200)}");

        var result = await tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Contains("Exit code: 0", result);
        Assert.Contains(new string('x', 200), result); // full output, not yet windowed/spilled
        Assert.DoesNotContain("saved to", result);      // ShellTool itself does not spill
    }

    [Fact]
    public async Task Working_directory_is_respected()
    {
        var tmpDir = Path.GetTempPath();
        var command = TestShellEnvironment.PrintWorkingDirectoryCommand;
        var args = ToolInput.Create("Command", command, "WorkingDirectory", tmpDir);

        var result = await _tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        // Normalize paths for comparison: resolve symlinks, trim trailing separators
        var resolvedTmpDir = Path.GetFullPath(tmpDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Contains("Exit code: 0", result);
        Assert.Contains(resolvedTmpDir, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_command_returns_error()
    {
        var args = ToolInput.Empty();
        var result = await _tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

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
            var context = TestToolExecutionContext.CreateBound("session-1", sessionDir, new TestToolExecutionContextOptions
            { Audience = TrustAudience.Personal, ProjectDirectory = projectDir });
            var args = ToolInput.Create("Command", TestShellEnvironment.PrintWorkingDirectoryCommand);

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
            var context = TestToolExecutionContext.CreateBound("session-1", sessionDir, TrustAudience.Personal);
            // ProjectDirectory not set
            var args = ToolInput.Create("Command", TestShellEnvironment.PrintWorkingDirectoryCommand);

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
    public async Task Cwd_creates_missing_session_directory_when_used_as_default()
    {
        var sessionDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var context = TestToolExecutionContext.CreateBound("session-1", sessionDir, TrustAudience.Personal);
            var args = ToolInput.Create("Command", TestShellEnvironment.PrintWorkingDirectoryCommand);

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.True(Directory.Exists(sessionDir));
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
            var context = TestToolExecutionContext.CreateBound("session-1", sessionDir, new TestToolExecutionContextOptions
            { Audience = TrustAudience.Personal, ProjectDirectory = projectDir });
            var args = ToolInput.Create(
                "Command", TestShellEnvironment.PrintWorkingDirectoryCommand,
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
            var context = TestToolExecutionContext.CreateBound("session-1", sessionDir, TrustAudience.Personal);
            var args = ToolInput.Create("Command", TestShellEnvironment.PrintWorkingDirectoryCommand);

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

    // ── Working directory must exist (#1286): fail loudly with the mkdir remedy ──
    // instead of letting Process.Start surface an opaque, platform-specific error.

    [Fact]
    public async Task Missing_explicit_working_directory_returns_helpful_error()
    {
        var missingDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var args = ToolInput.Create("Command", "echo hi", "WorkingDirectory", missingDir);

        var result = await _tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Contains("does not exist", result);
        Assert.Contains(missingDir, result);
        Assert.Contains(TestShellEnvironment.CreateDirectoryCommandName, result);
        // The process must never start with a missing cwd...
        Assert.DoesNotContain("Exit code", result);
        // ...and the tool must not silently create the directory either.
        Assert.False(Directory.Exists(missingDir));
    }

    [Fact]
    public async Task Working_directory_that_is_a_file_returns_not_a_directory_error()
    {
        var filePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        File.WriteAllText(filePath, "not a dir");
        try
        {
            var args = ToolInput.Create("Command", "echo hi", "WorkingDirectory", filePath);

            var result = await _tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

            Assert.Contains("is a file, not a directory", result);
            Assert.Contains(filePath, result);
            Assert.DoesNotContain("Exit code", result);
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Missing_project_directory_returns_helpful_error()
    {
        // No explicit arg, so the resolution chain falls back to ProjectDirectory —
        // proving the existence guard covers the fallback paths, not just explicit args.
        var sessionDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var missingProjectDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(sessionDir);
        try
        {
            var context = TestToolExecutionContext.CreateBound("session-1", sessionDir, new TestToolExecutionContextOptions
            { Audience = TrustAudience.Personal, ProjectDirectory = missingProjectDir });
            var args = ToolInput.Create("Command", "echo hi");

            var result = await _tool.ExecuteAsync(args, context, CancellationToken.None);

            Assert.Contains("does not exist", result);
            Assert.Contains(missingProjectDir, result);
            Assert.DoesNotContain("Exit code", result);
        }
        finally
        {
            if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Null_arguments_returns_error()
    {
        var result = await _tool.ExecuteAsync(null, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);
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
        var tool = new ShellTool(new ToolConfig(), policy, new ShellCommandPolicy());

        var args = ToolInput.Create("Command", $"cat {secretsPath}");

        var result = await tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Contains("protected file path", result);
        Assert.Contains("Access denied", result);
    }


    [Fact]
    public async Task High_risk_glob_on_netclaw_config_is_blocked()
    {
        var secretsPath = "/home/user/.netclaw/config/secrets.json";
        var policy = new ToolPathPolicy([secretsPath]);
        var tool = new ShellTool(new ToolConfig(), policy, new ShellCommandPolicy());

        var args = ToolInput.Create("Command", "cat ~/.netclaw/config/*.json");

        var result = await tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Contains("protected file path", result);
        Assert.Contains("Access denied", result);
    }

    [Fact]
    public async Task Hard_deny_blocks_daemon_stop()
    {
        var commandPolicy = new ShellCommandPolicy();
        var tool = new ShellTool(new ToolConfig(), new ToolPathPolicy([]), commandPolicy);

        var args = ToolInput.Create("Command", "netclaw daemon stop");
        var result = await tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Contains("hard deny policy", result);
    }

    [Fact]
    public async Task Hard_deny_blocks_kill_command()
    {
        var commandPolicy = new ShellCommandPolicy();
        var tool = new ShellTool(new ToolConfig(), new ToolPathPolicy([]), commandPolicy);

        var args = ToolInput.Create("Command", "kill -9 12345");
        var result = await tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.Contains("hard deny policy", result);
    }

    [Fact]
    public async Task Hard_deny_checked_before_path_policy()
    {
        var commandPolicy = new ShellCommandPolicy();
        var pathPolicy = new ToolPathPolicy(["/some/path"]);
        var tool = new ShellTool(new ToolConfig(), pathPolicy, commandPolicy);

        var args = ToolInput.Create("Command", "netclaw daemon stop");
        var result = await tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

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

        var result = await tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), CancellationToken.None);

        Assert.DoesNotContain("hard deny policy", result);
        Assert.Contains("protected file path", result);
        Assert.Contains("Access denied", result);
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic-link behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only symbolic-link semantics")]
    public async Task Authorized_execution_rechecks_current_symbolic_link_state()
    {
        var root = Directory.CreateTempSubdirectory("netclaw-shell-recheck-");
        try
        {
            var deniedDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "denied"));
            var deniedFile = Path.Combine(deniedDirectory.FullName, "secret.txt");
            await File.WriteAllTextAsync(
                deniedFile,
                "secret",
                TestContext.Current.CancellationToken);
            var link = Path.Combine(root.FullName, "late-link");
            var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
            var pathPolicy = new ToolPathPolicy(environment, [deniedDirectory.FullName]);
            var commandPolicy = new ShellCommandPolicy(environment);
            var tool = new ShellTool(new ToolConfig(), pathPolicy, commandPolicy);
            var command = $"cat {link}";
            var analysis = commandPolicy.Analyze(command, root.FullName);
            Assert.False(pathPolicy.CommandReferencesDeniedPath(analysis));

            File.CreateSymbolicLink(link, deniedFile);
            var result = await tool.ExecuteAuthorizedAsync(
                ToolInput.Create(
                    "Command",
                    command,
                    "WorkingDirectory",
                    root.FullName),
                TestToolExecutionContext.CreateUnbound().Invocation,
                analysis,
                TestContext.Current.CancellationToken);

            Assert.Contains("protected file path", result);
            Assert.Contains("Access denied", result);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
