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
    private readonly ShellTool _tool = new(new ToolConfig());

    private static async Task<(List<ToolActivityUpdate> Activities, ToolCompletedUpdate? Completion)>
        CollectStreamAsync(ShellTool tool, IDictionary<string, object?> args,
            ToolExecutionContext? context = null, CancellationToken ct = default)
    {
        var activities = new List<ToolActivityUpdate>();
        ToolCompletedUpdate? completion = null;

        await foreach (var update in tool.ExecuteStreamAsync(args, context ?? ToolExecutionContext.Empty, ct))
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
        var args = ToolInput.Create("Command", "echo error >&2");
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
        // Print lines with small delays to trigger multiple coalesce windows
        var args = ToolInput.Create("Command",
            "for i in 1 2 3; do echo \"line $i\"; sleep 0.6; done");
        var (activities, completion) = await CollectStreamAsync(_tool, args, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(completion);
        Assert.Contains("Exit code: 0", completion.Result);

        // Multiple activity items should be emitted across coalesce intervals
        var stdoutActivities = activities.Where(a => a.Phase == "stdout").ToList();
        Assert.True(stdoutActivities.Count >= 2,
            $"Expected >= 2 stdout activities for a chatty command, got {stdoutActivities.Count}");
    }

    [Fact]
    public async Task Cancellation_kills_process_and_returns_timeout()
    {
        using var cts = new CancellationTokenSource();
        var args = ToolInput.Create("Command", "sleep 100");

        // Cancel after a short delay
        cts.CancelAfter(TimeSpan.FromSeconds(1));

        var (_, completion) = await CollectStreamAsync(_tool, args, ct: cts.Token);

        Assert.NotNull(completion);
        Assert.Contains("timed out after", completion.Result);
    }

    [Fact]
    public async Task Output_clamping_preserved_in_completion_result()
    {
        var tool = new ShellTool(new ToolConfig { MaxOutputChars = 100 });
        // Generate output much larger than the 100-char budget
        var args = ToolInput.Create("Command", "seq 1 10000");
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
        var policy = new ShellCommandPolicy(["kill"], []);
        var tool = new ShellTool(new ToolConfig(), commandPolicy: policy);
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

        var nonStreaming = await _tool.ExecuteAsync(args, ToolExecutionContext.Empty, TestContext.Current.CancellationToken);
        var (_, completion) = await CollectStreamAsync(_tool, args, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(completion);
        Assert.Equal(nonStreaming, completion.Result);
    }
}
