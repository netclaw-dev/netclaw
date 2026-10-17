// -----------------------------------------------------------------------
// <copyright file="HeadlessWaitForChildrenTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Cli.Daemon;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using R3;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Pins the stop rule of <c>netclaw chat -p</c> when a turn starts child runs. The rule is in the
/// <c>netclaw-cli</c> spec and in SPEC-004 ("Waiting for child runs"): a run ends on the first completed
/// turn that reports no open child run, and a failed turn ends it at once. These tests feed the channel
/// through the daemon transport seam, with the field set by hand. That isolates the channel from the
/// session and from the mapper. <see cref="HeadlessAsyncChildEndToEndTests"/> drives the real session.
/// </summary>
/// <remarks>
/// No test waits on a delay. A script that must act after the channel handled a turn waits on a stdout
/// line. A test that pushes all turns at once relies on the order of the client event pump.
/// </remarks>
[Collection(HeadlessProcessStateCollection.Name)]
public sealed class HeadlessWaitForChildrenTests : IAsyncDisposable
{
    private const string Session = "fake/session";

    private readonly DisposableTempDir _directory = new();
    private readonly FakeDaemonHubTransport _transport = new();
    private readonly TaskCompletionSource _promptSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DaemonClient _client;

    public HeadlessWaitForChildrenTests()
    {
        _client = new("http://127.0.0.1:1", _transport, reconnectDelays: [TimeSpan.Zero]);
        _transport.VoidInvokeHook = (method, _, _) =>
        {
            if (method == "SendMessage")
                _promptSent.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _directory.Dispose();
    }

    // ----- The rule: wait while a run is open, end when none is -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_waits_for_the_turn_that_reports_no_open_child(bool json)
    {
        // Planted bug: the run ends on the first TurnCompleted, as it did before child runs were async.
        var run = await RunAsync(json, async drive =>
        {
            await _promptSent.Task;
            Push(Text("I started the child."), ToolCall("c1", "spawn_agent"), Usage(10, 4), Turn(1, "run-1"));
            Push(ToolCall("c2", "child_run_result"), Text("The child found 42."), Usage(7, 3), Turn(2));
        });

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.TurnFailedLines);
        Assert.Equal(1, CountOf(run.SessionLog, "SESSION_ENDED"));
        Assert.Equal(1, CountOf(run.SessionLog, "WAITING_FOR_CHILDREN: count=1 runs=run-1"));
        Assert.Equal(2, CountOf(run.SessionLog, "TURN_COMPLETED:"));

        if (!json)
        {
            Assert.Contains("[subagent:waiting] 1 open: run-1", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("I started the child.", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("The child found 42.", run.Stdout, StringComparison.Ordinal);
            Assert.True(
                run.Stdout.IndexOf("[subagent:waiting]", StringComparison.Ordinal)
                < run.Stdout.IndexOf("The child found 42.", StringComparison.Ordinal));
            return;
        }

        // JSON mode keeps stdout clean: one envelope, no status line.
        Assert.DoesNotContain("[subagent:waiting]", run.Stdout, StringComparison.Ordinal);
        using var envelope = Assert.Single(Envelopes(run));
        var root = envelope.RootElement;
        Assert.Equal("The child found 42.", root.GetProperty("response").GetString());
        var turns = root.GetProperty("turnResponses").EnumerateArray().ToArray();
        Assert.Equal(2, turns.Length);
        Assert.Equal("I started the child.", turns[0].GetProperty("text").GetString());
        Assert.Equal("The child found 42.", turns[1].GetProperty("text").GetString());
        Assert.Equal(1, turns[0].GetProperty("turnNumber").GetInt32());
        Assert.Equal(2, turns[1].GetProperty("turnNumber").GetInt32());
        Assert.Equal(
            ["spawn_agent", "child_run_result"],
            root.GetProperty("toolCalls").EnumerateArray().Select(call => call.GetProperty("toolName").GetString()).ToArray());
        Assert.Equal(
            ["spawn_agent"],
            turns[0].GetProperty("toolCalls").EnumerateArray().Select(call => call.GetProperty("toolName").GetString()).ToArray());
        // The top-level usage is the sum over all turns, not the last turn.
        Assert.Equal(17, root.GetProperty("usage").GetProperty("inputTokens").GetInt64());
        Assert.Equal(7, root.GetProperty("usage").GetProperty("outputTokens").GetInt64());
        Assert.Equal(10, turns[0].GetProperty("usage").GetProperty("inputTokens").GetInt64());
        Assert.Equal(7, turns[1].GetProperty("usage").GetProperty("inputTokens").GetInt64());
        Assert.False(root.TryGetProperty("errorCategory", out _));
    }

    [Fact]
    public async Task Two_ready_children_need_three_turns_and_the_run_ends_after_the_third()
    {
        // Planted bug: the run ends at the first turn that reports fewer open runs than before.
        var run = await RunAsync(json: false, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started both."), Turn(1, "run-a", "run-b"));
            Push(Text("First result."), Turn(2, "run-b"));
            Push(Text("Second result."), Turn(3));
        });

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("[subagent:waiting] 2 open: run-a,run-b", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("[subagent:waiting] 1 open: run-b", run.Stdout, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(run.Stdout, "[subagent:waiting]"));
        Assert.Contains("Second result.", run.Stdout, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(run.SessionLog, "TURN_COMPLETED:"));
    }

    [Fact]
    public async Task Two_ready_children_give_the_last_turn_text_as_the_json_response()
    {
        var run = await RunAsync(json: true, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started both."), Turn(1, "run-a", "run-b"));
            Push(Text("First result."), Turn(2, "run-b"));
            Push(Text("Second result."), Turn(3));
        });

        using var envelope = Assert.Single(Envelopes(run));
        Assert.Equal("Second result.", envelope.RootElement.GetProperty("response").GetString());
        Assert.Equal(3, envelope.RootElement.GetProperty("turnResponses").GetArrayLength());
    }

    [Fact]
    public async Task A_follow_up_turn_that_starts_a_new_child_keeps_the_run_waiting()
    {
        var run = await RunAsync(json: false, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started one."), Turn(1, "run-a"));
            Push(Text("Got one, started another."), Turn(2, "run-b"));
            Push(Text("All done."), Turn(3));
        });

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("[subagent:waiting] 1 open: run-b", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("All done.", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_skipped_turn_with_an_open_child_does_not_end_the_run()
    {
        // A skipped turn (a rejected input) is not the turn that delivers the child result.
        var run = await RunAsync(json: false, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started."), Turn(1, "run-a"));
            Push(SkippedTurn(2, "run-a"));
            Push(Text("Result delivered."), Turn(3));
        });

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Result delivered.", run.Stdout, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(run.Stdout, "[subagent:waiting]"));
    }

    // ----- Runs that must not wait -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_with_no_child_ends_at_its_first_turn_and_prints_no_waiting_line(bool json)
    {
        // Regression guard: the output of a run with no child run is the output of the earlier single-turn run.
        var run = await RunAsync(json, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Done."), Usage(10, 4), Turn(1));
        });

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("[subagent:waiting]", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("WAITING_FOR_CHILDREN", run.SessionLog, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(run.SessionLog, "SESSION_ENDED"));
        if (!json)
        {
            Assert.Contains("Done.", run.Stdout, StringComparison.Ordinal);
            return;
        }

        using var envelope = Assert.Single(Envelopes(run));
        var root = envelope.RootElement;
        Assert.Equal("Done.", root.GetProperty("response").GetString());
        Assert.Equal(10, root.GetProperty("usage").GetProperty("inputTokens").GetInt64());
        Assert.False(root.TryGetProperty("toolCalls", out _));
        Assert.False(root.TryGetProperty("errorCategory", out _));
        var only = Assert.Single(root.GetProperty("turnResponses").EnumerateArray());
        Assert.Equal("Done.", only.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_turn_without_the_open_runs_field_comes_from_an_old_daemon_and_ends_the_run(bool json)
    {
        // An old daemon runs spawn_agent inside one turn and sends no field. Planted bug: a missing field is
        // read as "open", so the run waits for a turn that never comes.
        var run = await RunAsync(json, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Done."), TurnWithoutField(1));
        });

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("[subagent:waiting]", run.Stdout, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(run.SessionLog, "SESSION_ENDED"));
        if (json)
            Assert.Equal("Done.", Assert.Single(Envelopes(run)).RootElement.GetProperty("response").GetString());
    }

    // ----- Failed turns end the run at once -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_turn_with_an_open_child_ends_the_run_and_names_the_open_run(bool json)
    {
        // Planted bugs: the run waits after a failed turn, or the open-runs line is missing.
        var run = await RunAsync(json, async drive =>
        {
            await _promptSent.Task;
            Push(Error("ProviderFailure", "Service Unavailable"), FailedTurn(1, "run-1"));
        });

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=ProviderFailure"], run.TurnFailedLines);
        Assert.Equal([$"[headless:open-runs] session={Session} runs=run-1"], OpenRunsLines(run));
        Assert.True(
            run.Stderr.IndexOf("[turn:failed]", StringComparison.Ordinal)
            < run.Stderr.IndexOf("[headless:open-runs]", StringComparison.Ordinal));
        Assert.DoesNotContain("[subagent:waiting]", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("[headless:open-runs]", run.Stdout, StringComparison.Ordinal);
        if (json)
        {
            using var envelope = Assert.Single(Envelopes(run));
            Assert.Equal("infra", envelope.RootElement.GetProperty("errorCategory").GetString());
        }
    }

    [Fact]
    public async Task A_failed_turn_with_no_open_child_prints_no_open_runs_line()
    {
        var run = await RunAsync(json: false, async drive =>
        {
            await _promptSent.Task;
            Push(Error("ProviderFailure", "Service Unavailable"), FailedTurn(1));
        });

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(OpenRunsLines(run));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_follow_up_turn_ends_the_run_and_later_output_is_ignored(bool json)
    {
        // SPEC-004: exactly one [turn:failed] line and one envelope. Planted bug: the run keeps going after
        // the failed turn, so it prints the output of a later turn. The failed follow-up turn lists no run,
        // because the session closes the run when the delivery fails. The channel then names no open run.
        var run = await RunAsync(json, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started."), Turn(1, "run-1"));
            Push(Error("ProviderFailure", "Service Unavailable"), FailedTurn(2));
            Push(Text("Output of a later turn."), Turn(3));
        });

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=ProviderFailure"], run.TurnFailedLines);
        Assert.Empty(OpenRunsLines(run));
        Assert.DoesNotContain("Output of a later turn.", run.Stdout, StringComparison.Ordinal);
        if (json)
        {
            using var envelope = Assert.Single(Envelopes(run));
            Assert.Equal(2, envelope.RootElement.GetProperty("turnResponses").GetArrayLength());
            Assert.Equal("infra", envelope.RootElement.GetProperty("errorCategory").GetString());
        }
    }

    // ----- Stops during the wait -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_deactivation_during_the_wait_ends_the_run_loudly(bool json)
    {
        // Planted bug: the client reads the output as an unknown type and keeps waiting. A daemon restart
        // would then hang the run.
        var run = await RunAsync(json, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started."), Turn(1, "run-1"));
            Push(Deactivated());
        });

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=ChannelFailure"], run.TurnFailedLines);
        Assert.Contains("[headless:error]", run.Stderr, StringComparison.Ordinal);
        Assert.Equal([$"[headless:open-runs] session={Session} runs=run-1"], OpenRunsLines(run));
        if (json)
            Assert.Empty(Envelopes(run));
    }

    [Fact]
    public async Task A_signal_during_the_wait_ends_the_run_and_names_the_open_run()
    {
        using var stopping = new CancellationTokenSource();
        var run = await RunAsync(json: false, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started."), Turn(1, "run-1"));
            // The waiting line proves that the channel handled turn 1 and holds the open run.
            if (await drive.StdoutShowsAsync("[subagent:waiting]"))
                await stopping.CancelAsync();
        }, stopping);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=Cancelled"], run.TurnFailedLines);
        Assert.Equal([$"[headless:open-runs] session={Session} runs=run-1"], OpenRunsLines(run));
        Assert.True(
            run.Stderr.IndexOf("[turn:failed]", StringComparison.Ordinal)
            < run.Stderr.IndexOf("[headless:open-runs]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_signal_during_the_wait_writes_no_json_envelope()
    {
        // The signal can come before or after the channel handles turn 1. In both orders the run is
        // cancelled, so stdout holds no envelope and the exit code is 1.
        using var stopping = new CancellationTokenSource();
        var run = await RunAsync(json: true, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started."), Turn(1, "run-1"));
            await stopping.CancelAsync();
        }, stopping);

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(Envelopes(run));
        Assert.Equal(["[turn:failed] category=infra kind=Cancelled"], run.TurnFailedLines);
    }

    [Fact]
    public async Task A_signal_before_the_first_completed_turn_names_no_open_run()
    {
        // The channel knows the open runs only from a completed turn. Before that it names none.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var run = await RunAsync(json: false, _ => Task.CompletedTask, cancelled);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=Cancelled"], run.TurnFailedLines);
        Assert.Empty(OpenRunsLines(run));
    }

    // ----- A lost connection (SPEC-004, "A lost connection") -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_terminal_disconnect_during_the_wait_ends_the_run_loudly(bool json)
    {
        // Planted bug: the channel only logs the Disconnected state. The hub drops output while no client
        // is connected, so no turn comes and the run waits for ever with no stderr line.
        var run = await RunAsync(json, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started."), Turn(1, "run-1"));
            // The client pump keeps the order of the turn and the connection states. In text mode the waiting
            // line proves that the channel handled turn 1. JSON mode prints no such line.
            if (!json && !await drive.StdoutShowsAsync("[subagent:waiting]"))
                return;

            // Every reconnect attempt fails. The zero delay runs the whole budget at once.
            _transport.StartHook = _ => throw new InvalidOperationException("connection refused");
            _transport.RaiseClosed(new IOException("The network dropped."));
        });

        AssertLostConnection(run, json, expectOpenRun: true);
    }

    [Fact]
    public async Task A_transport_close_during_the_wait_ends_the_run_without_waiting_for_the_reconnect()
    {
        // Planted bug: the channel waits through the reconnect. A turn that completed in the gap is lost, so
        // the run waits for ever. The reconnect here would succeed: StartHook stays at its default.
        var run = await RunAsync(json: false, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started."), Turn(1, "run-1"));
            if (await drive.StdoutShowsAsync("[subagent:waiting]"))
                _transport.RaiseClosed(new IOException("The network dropped."));
        });

        AssertLostConnection(run, json: false, expectOpenRun: true);
    }

    [Fact]
    public async Task A_terminal_disconnect_before_the_first_turn_ends_the_run_and_names_no_open_run()
    {
        var run = await RunAsync(json: false, async drive =>
        {
            await _promptSent.Task;
            _transport.StartHook = _ => throw new InvalidOperationException("connection refused");
            _transport.RaiseClosed(new IOException("The network dropped."));
        });

        AssertLostConnection(run, json: false, expectOpenRun: false);
    }

    [Fact]
    public async Task A_transport_close_before_any_open_run_is_known_does_not_end_the_run()
    {
        // The run knows no open run, and the client reconnect covers the turn in progress. The run keeps its
        // subscription and ends on the next turn.
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = _client.ConnectionEvents.Subscribe(evt =>
        {
            if (evt.State == DaemonConnectionState.Connected
                && evt.Message.Contains("Reconnected", StringComparison.Ordinal))
                reconnected.TrySetResult();
        });

        var run = await RunAsync(json: false, async drive =>
        {
            await _promptSent.Task;
            _transport.RaiseClosed(new IOException("The network dropped."));
            await reconnected.Task.WaitAsync(TestContext.Current.CancellationToken);
            Push(Text("Done."), Turn(1));
        });

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.TurnFailedLines);
        Assert.DoesNotContain("[headless:error]", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("Done.", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disconnect_after_the_final_turn_does_not_change_a_successful_exit()
    {
        var run = await RunAsync(json: false, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Done."), Turn(1));
            _transport.StartHook = _ => throw new InvalidOperationException("connection refused");
            _transport.RaiseClosed(new IOException("The network dropped."));
        });

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.TurnFailedLines);
        Assert.DoesNotContain("[headless:error]", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(OpenRunsLines(run));
        Assert.Contains("Done.", run.Stdout, StringComparison.Ordinal);
    }

    // ----- JSON usage rates -----

    [Fact]
    public async Task The_usage_rates_come_from_the_last_turn_with_usage_even_when_that_rate_is_null()
    {
        // SPEC-004: the counts add up. A rate does not, so the last turn with a usage record decides it.
        // That record has no rate here, so the rate is null. An earlier rate does not fill it in.
        var run = await RunAsync(json: true, async drive =>
        {
            await _promptSent.Task;
            Push(Text("Started."), Usage(10, 5, promptMs: 120, perSecond: 40), Turn(1, "run-1"));
            Push(Text("Result."), Usage(7, 3), Turn(2));
        });

        using var envelope = Assert.Single(Envelopes(run));
        var usage = envelope.RootElement.GetProperty("usage");
        Assert.Equal(17, usage.GetProperty("inputTokens").GetInt64());
        Assert.False(
            usage.TryGetProperty("promptMs", out var promptMs) && promptMs.ValueKind != JsonValueKind.Null,
            "The rate of an earlier turn must not fill in a null rate.");
        Assert.False(
            usage.TryGetProperty("predictedPerSecond", out var perSecond) && perSecond.ValueKind != JsonValueKind.Null,
            "The rate of an earlier turn must not fill in a null rate.");
    }

    // ----- Helpers -----

    // One [headless:error] line, one [turn:failed] ChannelFailure line, and the open-runs line when a run is
    // known. A lost connection writes no JSON envelope.
    private static void AssertLostConnection(HeadlessRun run, bool json, bool expectOpenRun)
    {
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=ChannelFailure"], run.TurnFailedLines);
        Assert.Contains("[headless:error]", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(
            expectOpenRun ? [$"[headless:open-runs] session={Session} runs=run-1"] : Array.Empty<string>(),
            OpenRunsLines(run));
        if (json)
            Assert.Empty(Envelopes(run));
    }

    private Task<HeadlessRun> RunAsync(bool json, Func<HeadlessDrive, Task> script, CancellationTokenSource? stop = null)
        => HeadlessRunHarness.RunAsync(
            _client, _directory, json, stop?.Token ?? TestContext.Current.CancellationToken, drive: script);

    private void Push(params SessionOutputDto[] outputs)
    {
        foreach (var output in outputs)
            _transport.PushOutput(output);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static IReadOnlyList<string> OpenRunsLines(HeadlessRun run) => run.Stderr
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.StartsWith("[headless:open-runs]", StringComparison.Ordinal))
        .ToArray();

    // Every line of stdout that is a JSON envelope. The in-process actor system writes its own lines to the
    // same console, so a line counts only when it parses and holds a session id.
    private static List<JsonDocument> Envelopes(HeadlessRun run)
    {
        var found = new List<JsonDocument>();
        foreach (var line in run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            if (!line.StartsWith('{') || !line.EndsWith('}'))
                continue;
            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("sessionId", out _))
                found.Add(document);
            else
                document.Dispose();
        }

        return found;
    }

    private static SessionOutputDto Text(string text)
        => SessionOutputDtoMapper.ToDto(new TextOutput(text) { SessionId = new SessionId(Session) });

    private static SessionOutputDto ToolCall(string callId, string tool)
        => SessionOutputDtoMapper.ToDto(new ToolCallOutput
        {
            SessionId = new SessionId(Session),
            CallId = new ToolCallId(callId),
            ToolName = new ToolName(tool),
            ArgumentsJson = "{}"
        });

    private static SessionOutputDto Usage(long input, long output, double? promptMs = null, double? perSecond = null)
        => SessionOutputDtoMapper.ToDto(new UsageOutput
        {
            SessionId = new SessionId(Session),
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = input + output,
            PromptMs = promptMs,
            PredictedPerSecond = perSecond
        });

    private static SessionOutputDto Error(string kind, string message)
        => SessionOutputDtoMapper.ToDto(new ErrorOutput
        {
            SessionId = new SessionId(Session),
            Message = message,
            Category = ErrorCategory.Unknown
        }) with
        {
            ErrorCategory = kind
        };

    private static SessionOutputDto Deactivated()
        => new() { Type = "session_deactivated", SessionId = Session };

    // The field is set on the DTO by hand, so these tests do not depend on the mapper.
    private static SessionOutputDto Turn(int number, params string[] open)
        => TurnWithoutField(number) with { OpenChildRunIds = [.. open] };

    private static SessionOutputDto FailedTurn(int number, params string[] open)
        => TurnWithoutField(number, TurnOutcome.Failed) with { OpenChildRunIds = [.. open] };

    private static SessionOutputDto SkippedTurn(int number, params string[] open)
        => TurnWithoutField(number, TurnOutcome.Skipped) with { OpenChildRunIds = [.. open] };

    private static SessionOutputDto TurnWithoutField(int number, TurnOutcome outcome = TurnOutcome.Completed)
        => SessionOutputDtoMapper.ToDto(new TurnCompleted
        {
            SessionId = new SessionId(Session),
            TurnNumber = new TurnNumber(number),
            Outcome = outcome,
            OpenChildRunIds = []
        }) with
        {
            OpenChildRunIds = null
        };
}
