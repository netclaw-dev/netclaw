// -----------------------------------------------------------------------
// <copyright file="ShellToolStreamingTests.cs" company="Petabridge, LLC">
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

public class ShellToolStreamingTests
{
    private static readonly ShellExecutionEnvironment ShellEnvironment = TestShellEnvironment.Current;
    private readonly ShellTool _tool = CreateTool();

    private static ShellTool CreateTool(ToolConfig? config = null)
    {
        var commandPolicy = new ShellCommandPolicy(ShellEnvironment);
        return new ShellTool(
            config ?? new ToolConfig(),
            new ToolPathPolicy(ShellEnvironment, []),
            commandPolicy);
    }

    [Fact]
    public async Task Missing_selected_executable_streams_failure_without_fallback()
    {
        const string missingExecutable = @"C:\missing\pwsh.exe";
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            missingExecutable,
            ShellSyntaxTree.PwshDialect.PowerShell7);
        var tool = new ShellTool(
            new ToolConfig(),
            new ToolPathPolicy(environment, []),
            new ShellCommandPolicy(environment));

        var (activities, completion) = await CollectStreamAsync(
            tool,
            ToolInput.Create("Command", "Get-ChildItem"),
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(activities);
        Assert.NotNull(completion);
        Assert.Contains(missingExecutable, completion.Result);
        Assert.DoesNotContain("powershell.exe", completion.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", completion.Result, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(List<ToolActivityUpdate> Activities, ToolCompletedUpdate? Completion)>
        CollectStreamAsync(ShellTool tool, IDictionary<string, object?> args,
            ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        var activities = new List<ToolActivityUpdate>();
        ToolCompletedUpdate? completion = null;

        await foreach (var update in tool.ExecuteStreamAsync(args, context ?? TestToolExecutionContext.CreateUnbound(), ct))
        {
            switch (update)
            {
                case ToolActivityUpdate activity:
                    activities.Add(activity);
                    break;
                case ToolCompletedUpdate completed:
                    completion = completed;
                    break;
            }
        }

        return (activities, completion);
    }

    [Fact]
    public async Task Echo_emits_activity_with_output_chunk_then_completion()
    {
        var args = ToolInput.Create("Command", "echo hello");
        var (activities, completion) = await CollectStreamAsync(_tool, args, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(completion);
        Assert.Contains("Exit code: 0", completion.Result);
        Assert.Contains("hello", completion.Result);

        // At least one activity item should carry the output
        Assert.NotEmpty(activities);
        var withOutput = activities.Where(a => a.OutputChunk is not null).ToList();
        Assert.NotEmpty(withOutput);
        Assert.Contains(withOutput, a => a.OutputChunk!.Contains("hello"));
    }

    [Fact]
    public async Task Stderr_emits_activity_items_with_stderr_phase()
    {
        var args = ToolInput.Create("Command", TestShellEnvironment.StandardErrorCommand);
        var (activities, completion) = await CollectStreamAsync(_tool, args, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(completion);
        Assert.Contains("Exit code: 0", completion.Result);

        var stderrActivities = activities.Where(a => a.Phase == "stderr").ToList();
        Assert.NotEmpty(stderrActivities);
        Assert.Contains(stderrActivities, a => a.OutputChunk is not null && a.OutputChunk.Contains("error"));
    }

    [Fact]
    public async Task Chatty_command_emits_multiple_activities()
    {
        // Produce enough output that pipe reads span multiple coalesce
        // windows without relying on sleep-based timing.
        var cmd = ShellEnvironment.Grammar == ShellGrammar.PowerShell
            ? "1..200 | ForEach-Object { \"line $_\" }"
            : "for i in $(seq 1 200); do echo \"line $i\"; done";
        var args = ToolInput.Create("Command", cmd);
        var (activities, completion) = await CollectStreamAsync(_tool, args, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(completion);
        Assert.Contains("Exit code: 0", completion.Result);

        var stdoutActivities = activities.Where(a => a.Phase == "stdout").ToList();
        Assert.NotEmpty(stdoutActivities);
    }

    [Fact]
    public async Task Cancellation_kills_process_and_returns_timeout()
    {
        using var cts = new CancellationTokenSource();
        var cmd = TestShellEnvironment.LongRunningCommand;
        var args = ToolInput.Create("Command", cmd);

        // Cancel after a short delay
        cts.CancelAfter(TimeSpan.FromSeconds(1));

        var (_, completion) = await CollectStreamAsync(_tool, args, ct: cts.Token);

        Assert.NotNull(completion);
        Assert.Contains("timed out after", completion.Result);
    }

    [Fact]
    public async Task Output_clamping_preserved_in_completion_result()
    {
        var tool = CreateTool(new ToolConfig { MaxOutputChars = 100 });
        // Generate output much larger than the 100-char budget
        var cmd = ShellEnvironment.Grammar == ShellGrammar.PowerShell
            ? "1..10000"
            : "seq 1 10000";
        var args = ToolInput.Create("Command", cmd);
        var (_, completion) = await CollectStreamAsync(tool, args, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(completion);
        Assert.Contains("Exit code: 0", completion.Result);
        Assert.Contains("...", completion.Result);
    }

    [Fact]
    public async Task Empty_command_yields_immediate_error_completion()
    {
        var args = ToolInput.Create("Command", "");
        var (activities, completion) = await CollectStreamAsync(_tool, args, ct: TestContext.Current.CancellationToken);

        Assert.Empty(activities);
        Assert.NotNull(completion);
        Assert.Contains("required", completion.Result);
    }

    [Fact]
    public async Task Hard_deny_yields_immediate_error_completion()
    {
        var policy = new ShellCommandPolicy(ShellEnvironment, ["kill"], []);
        var tool = new ShellTool(new ToolConfig(), new ToolPathPolicy(ShellEnvironment, []), policy);
        var command = ShellEnvironment.Grammar == ShellGrammar.PowerShell
            ? "Stop-Process -Id 1"
            : "kill -9 1";
        var args = ToolInput.Create("Command", command);
        var (activities, completion) = await CollectStreamAsync(tool, args, ct: TestContext.Current.CancellationToken);

        Assert.Empty(activities);
        Assert.NotNull(completion);
        Assert.Contains("blocked", completion.Result);
    }

    [Fact]
    public async Task Streaming_result_matches_non_streaming_format()
    {
        var args = ToolInput.Create("Command", TestShellEnvironment.TwoOutputLinesCommand);

        var nonStreaming = await _tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), TestContext.Current.CancellationToken);
        var (_, completion) = await CollectStreamAsync(_tool, args, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(completion);
        Assert.Equal(nonStreaming, completion.Result);
    }
}
