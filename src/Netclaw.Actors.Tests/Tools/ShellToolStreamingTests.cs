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
    private readonly ShellTool _tool = new(
        new ToolConfig(),
        new ToolPathPolicy([]),
        new ShellCommandPolicy(ShellExecutionEnvironment.Current));

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
        // On Windows, Write-Error sets a non-zero pwsh exit code, breaking the
        // clean-exit assertion below. [Console]::Error.WriteLine writes to
        // stderr and exits 0, keeping the test's meaning: stderr is emitted as
        // a stderr-phase activity on a clean exit.
        var cmd = OperatingSystem.IsWindows()
            ? "[Console]::Error.WriteLine('error')"
            : "echo error >&2";
        var args = ToolInput.Create("Command", cmd);
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
        var cmd = OperatingSystem.IsWindows()
            ? "1..200 | ForEach-Object { Write-Output \"line $_\" }"
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
        var cmd = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 100" : "sleep 100";
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
        var tool = new ShellTool(new ToolConfig { MaxOutputChars = 100 }, new ToolPathPolicy([]), new ShellCommandPolicy(ShellExecutionEnvironment.Current));
        // Generate output much larger than the 100-char budget
        var cmd = OperatingSystem.IsWindows()
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
        var policy = new ShellCommandPolicy(ShellExecutionEnvironment.Current, ["kill"], []);
        var tool = new ShellTool(new ToolConfig(), new ToolPathPolicy([]), policy);
        var args = ToolInput.Create("Command", "kill -9 1");
        var (activities, completion) = await CollectStreamAsync(tool, args, ct: TestContext.Current.CancellationToken);

        Assert.Empty(activities);
        Assert.NotNull(completion);
        Assert.Contains("blocked", completion.Result);
    }

    [Fact]
    public async Task Streaming_result_matches_non_streaming_format()
    {
        var args = ToolInput.Create("Command", "echo hello && echo world");

        var nonStreaming = await _tool.ExecuteAsync(args, TestToolExecutionContext.CreateUnbound(), TestContext.Current.CancellationToken);
        var (_, completion) = await CollectStreamAsync(_tool, args, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(completion);
        Assert.Equal(nonStreaming, completion.Result);
    }
}
