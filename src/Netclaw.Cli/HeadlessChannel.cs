// -----------------------------------------------------------------------
// <copyright file="HeadlessChannel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Channels;
using Netclaw.Cli.Daemon;
using Netclaw.Tools;
using R3;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli;

/// <summary>
/// Headless channel for single-prompt mode (<c>chat -p</c>).
/// Sends one message to the LLM session and streams all output to stdout. The run ends on the first
/// <see cref="TurnCompleted"/> that reports no open child run, or on a failed turn. While a child run is
/// open, the run waits for the follow-up parent turn that delivers its result.
/// </summary>
public sealed class HeadlessChannel : IChannel
{
    private readonly DaemonClient _daemonClient;
    private readonly NetclawPaths _paths;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly string _prompt;
    private readonly string? _resumeSessionId;
    private readonly bool _jsonOutput;
    private readonly ILogger<HeadlessChannel> _logger;

    private bool _isConnected;
    private bool _receivedTextDeltaInCurrentTurn;
    private bool _receivedThinkingDeltaInCurrentTurn;

    // JSON output accumulation. The buffer, the tool calls, and the usage belong to the turn in progress.
    // A TurnCompleted moves them into _turns.
    private readonly StringBuilder _responseBuffer = new();
    private readonly List<JsonToolCall> _turnToolCalls = [];
    private JsonUsage? _turnUsage;
    private readonly List<JsonTurn> _turns = [];
    private string? _resolvedSessionId;

    // The output pump and the signal path both end the run. _gate makes the end happen once: after _ended,
    // the channel ignores every output, so a failed run prints one failure line and a JSON run one envelope.
    private readonly object _gate = new();
    private readonly TaskCompletionSource _runEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _ended;

    // The run thread. RunHeadlessAsync catches every exception, so this task does not fault.
    private Task _run = Task.CompletedTask;

    // True once the run thread is about to send the prompt. Only then can the hub hold output for this run.
    // A connection state before that point does not end the run: the request that the run thread waits for
    // fails with its own reason when the connection cannot be restored.
    private volatile bool _promptSent;

    // Set under the log lock when the run thread closes the log writer. A late write then does nothing.
    private bool _logClosed;

    // The open child runs of the most recent completed turn. Empty before the first one. The channel stores
    // them before it prints the waiting line, so a signal that follows that line finds them.
    private OpenRunsSnapshot _openRuns = OpenRunsSnapshot.None;

    // The kind of the most recent ErrorOutput in the current turn. The error that precedes a failed
    // TurnCompleted decides the reported kind; an earlier error that the turn survived must not.
    private ErrorCategory? _lastErrorKind;

    // The prompts that this run printed. Only their closes are printed.
    private readonly HashSet<ToolCallId> _shownPrompts = [];

    // Client-side timing
    private long _promptSentTicks;
    private long _firstDeltaTicks;

    /// <summary>
    /// The process exit code for this run: 1 once the session reports a failed turn
    /// (provider unreachable, timeout, tool failure, model behavior), otherwise 0.
    /// A failed run or a cancelled run also prints the <see cref="HeadlessFailure"/> token.
    /// The channel writes it under the gate, in the same step that marks the run ended.
    /// </summary>
    internal int ExitCode { get; private set; }

    public Actors.Channels.ChannelType ChannelType => Actors.Channels.ChannelType.Headless;
    public string DisplayName => "Headless Prompt";

    public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = _isConnected
            ? new ChannelHealth(ChannelHealthStatus.Healthy)
            : new ChannelHealth(ChannelHealthStatus.Disconnected, "No active daemon connection");

        return ValueTask.FromResult(health);
    }

    public HeadlessChannel(
        DaemonClient daemonClient,
        NetclawPaths paths,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider,
        HeadlessOptions options,
        ILogger<HeadlessChannel> logger)
    {
        _daemonClient = daemonClient;
        _paths = paths;
        _lifetime = lifetime;
        _timeProvider = timeProvider;
        _prompt = options.Prompt;
        _resumeSessionId = options.ResumeSessionId;
        _jsonOutput = options.JsonOutput;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = Task.Run(() => RunHeadlessAsync(_lifetime.ApplicationStopping), CancellationToken.None);
        return Task.CompletedTask;
    }

    // The run thread owns the session log writer. It calls StopApplication first and closes the writer after that.
    // StopAsync waits for the run thread, so the writer is closed when StopAsync returns. Windows cannot delete an
    // open file, so a caller that deletes the logs folder after the stop needs this order (#2597).
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _isConnected = false;
        await _run.WaitAsync(cancellationToken);
    }

    private async Task RunHeadlessAsync(CancellationToken stopping)
    {
        // Log writer is deferred until we know the session ID (after create/resume).
        // Volatile ensures the subscription callback sees the assigned value across threads.
        StreamWriter? logWriter = null;

        try
        {
            _paths.EnsureDirectoriesExist();

            // Refuse before connecting so an over-limit prompt does not leave an empty session behind.
            if (SessionHubFrame.CheckText(_prompt) is { } tooLarge)
                throw new InvalidOperationException(tooLarge);

            using var connectionSubscription = _daemonClient.ConnectionEvents.Subscribe(evt =>
            {
                OnConnectionEvent(evt, Volatile.Read(ref logWriter));
            });

            using var subscription = _daemonClient.SessionOutput.Subscribe(output =>
            {
                HandleOutput(output, Volatile.Read(ref logWriter));
            });

            await _daemonClient.ConnectAsync(stopping);
            _isConnected = true;

            // Create or resume session
            string sessionIdValue;
            if (_resumeSessionId is not null)
            {
                sessionIdValue = await _daemonClient.ResumeSessionAsync(
                    _resumeSessionId, ChannelType, stopping);
            }
            else
            {
                sessionIdValue = await _daemonClient.CreateSessionAsync(ChannelType, stopping);
            }

            _resolvedSessionId = sessionIdValue;
            var sessionId = new SessionId(sessionIdValue);

            var logFileName = $"{sessionId.Value.Replace("/", "-", StringComparison.Ordinal)}.log";
            var logPath = Path.Combine(_paths.LogsDirectory, logFileName);
            Volatile.Write(ref logWriter, new StreamWriter(logPath, append: true) { AutoFlush = true });

            Log(logWriter, $"Headless session started: {sessionId}");
            Log(logWriter, $"PROMPT: {_prompt}");

            _promptSentTicks = Stopwatch.GetTimestamp();
            _promptSent = true;

            await _daemonClient.SendAsync(_prompt, stopping);

            _logger.LogInformation("Headless session started: {SessionId} (log: {LogPath})", sessionId, logPath);

            await _runEnded.Task.WaitAsync(stopping);
            FinishRun();
        }
        catch (OperationCanceledException ex) when (stopping.IsCancellationRequested)
        {
            // SIGINT or SIGTERM. Exit 1: exit 0 with no output reads as a pass to a script.
            _logger.LogDebug(ex, "Headless channel cancelled (shutdown)");
            if (TryBeginStop(out var openRuns))
            {
                WriteFailureLog("CANCELLED", ex.ToString());
                Console.Error.WriteLine(HeadlessFailure.Cancelled.ToToken());
                openRuns.WriteLine();
            }

            FinishRun();
        }
        catch (Exception ex)
        {
            // A cancellation that did not come from shutdown (for example a transport timeout)
            // lands here too. It is a channel failure, not a clean stop.
            _logger.LogError(ex, "Headless channel failed");
            if (TryBeginStop(out var openRuns))
            {
                Console.Error.WriteLine($"[headless:error] {SingleLine(ex.Message)}");
                Console.Error.WriteLine(HeadlessFailure.Channel.ToToken());
                openRuns.WriteLine();
                WriteFailureLog("FAILED", ex.ToString());
            }

            FinishRun();
        }
        finally
        {
            // A callback on the client pump can still write while the run thread leaves. The lock makes the
            // close wait for a write in progress, and the flag makes a later write do nothing.
            if (logWriter is not null)
            {
                lock (logWriter)
                {
                    _logClosed = true;
                    logWriter.Dispose();
                }
            }
        }
    }

    // The run ends once. A turn that ended it, or a stop that began, already holds the result. FinishRun then
    // only passes the exit code on, whichever path ended the run.
    private void FinishRun()
    {
        // Read under the gate: the thread that ended the run wrote the code under the same lock.
        int exitCode;
        lock (_gate)
            exitCode = ExitCode;
        if (exitCode != 0)
            Environment.ExitCode = exitCode;
        _lifetime.StopApplication();
    }

    // Marks the run ended for a signal or a channel failure, and sets exit code 1 in the same step. Every caller
    // is a failure path, and a thread that loses the race must still see the code. Returns false when another
    // path already ended the run: that result stands, and the caller prints nothing more.
    private bool TryBeginStop(out OpenRunsSnapshot openRuns)
    {
        lock (_gate)
        {
            openRuns = _openRuns;
            if (_ended)
                return false;
            _ended = true;
            ExitCode = 1;
            return true;
        }
    }

    // The hub drops session output while no client is connected, and a reconnect does not replay it. A turn
    // that completes in that gap is lost, so the run would wait for ever or end with incomplete output. After
    // the prompt, both Disconnected and TransportClosed therefore end the run loudly, with or without a known
    // open child run. The operator resumes the session with `netclaw chat --resume`.
    //   Disconnected: the client spent its whole retry budget, or a bind failed. No more output can arrive.
    //   TransportClosed: the client starts to reconnect, but a reconnect restores no output from the gap.
    // Before the prompt, nothing is in flight. The request that the run thread waits for fails with its own
    // reason, and the run thread ends the run with that reason. After the run ended, both states change
    // nothing and write nothing: a disconnect after the final turn keeps exit 0, and the log writer may be
    // closed. One lock holds the end check, the log write, and the mark, so the callback and the run thread
    // cannot both end the run.
    private void OnConnectionEvent(DaemonConnectionEvent evt, StreamWriter? log)
    {
        OpenRunsSnapshot openRuns;
        lock (_gate)
        {
            if (_ended)
                return;

            Log(log, $"CONNECTION: {evt.Message}");
            if (evt.State is not (DaemonConnectionState.TransportClosed or DaemonConnectionState.Disconnected)
                || !_promptSent)
                return;

            _ended = true;
            ExitCode = 1;
            openRuns = _openRuns;
        }

        var detail = $"Lost the connection to the daemon before the run ended: {evt.Message}";
        Console.Error.WriteLine($"[headless:error] {SingleLine(detail)}");
        Console.Error.WriteLine(HeadlessFailure.Channel.ToToken());
        openRuns.WriteLine();
        WriteFailureLog("CONNECTION_LOST", detail);
        EndRun(log, "CONNECTION_LOST");
    }

    internal void HandleOutput(SessionOutput output, StreamWriter? log)
    {
        lock (_gate)
        {
            if (_ended)
                return;
            HandleOutputCore(output, log);
        }
    }

    private void HandleOutputCore(SessionOutput output, StreamWriter? log)
    {
        switch (output)
        {
            case SessionJoined msg:
                Log(log, $"SESSION_JOINED turn_count={msg.TurnCount} title={msg.Title ?? "(none)"}");
                break;

            case TextOutput msg:
                if (_receivedTextDeltaInCurrentTurn)
                {
                    Log(log, $"ASSISTANT_FINAL: {msg.Text}");
                    break;
                }

                if (_jsonOutput)
                    _responseBuffer.Append(msg.Text);
                else
                    Console.WriteLine(msg.Text);
                Log(log, $"ASSISTANT: {msg.Text}");
                break;

            case TextDeltaOutput msg:
                if (!_receivedTextDeltaInCurrentTurn && _promptSentTicks > 0)
                    Interlocked.CompareExchange(ref _firstDeltaTicks, Stopwatch.GetTimestamp(), 0);
                _receivedTextDeltaInCurrentTurn = true;
                if (_jsonOutput)
                    _responseBuffer.Append(msg.Delta);
                else
                    Console.Write(msg.Delta);
                Log(log, $"ASSISTANT_DELTA: {msg.Delta}");
                break;

            case ThinkingOutput msg:
                if (_receivedThinkingDeltaInCurrentTurn)
                {
                    Log(log, $"THINKING_FINAL: {msg.Text}");
                    break;
                }

                // Don't write thinking tokens to stdout — only log them
                Log(log, $"THINKING: {msg.Text}");
                break;

            case ThinkingDeltaOutput msg:
                _receivedThinkingDeltaInCurrentTurn = true;
                Log(log, $"THINKING_DELTA: {msg.Delta}");
                break;

            case ToolCallOutput msg:
                if (_jsonOutput)
                {
                    _turnToolCalls.Add(new JsonToolCall
                    {
                        CallId = msg.CallId.Value,
                        ToolName = msg.ToolName.Value,
                        ArgumentsJson = msg.ArgumentsJson
                    });
                }
                else
                {
                    Console.WriteLine($"[tool:call] {msg.ToolName}({msg.ArgumentsJson ?? ""})");
                }
                Log(log, $"TOOL_CALL: {msg.ToolName} call_id={msg.CallId} args={msg.ArgumentsJson ?? "{}"}");
                break;

            case ToolResultOutput msg:
                if (!_jsonOutput)
                    Console.WriteLine($"[tool:result] {msg.ToolName} \u2192 {msg.Result}");
                Log(log, $"TOOL_RESULT: {msg.ToolName} call_id={msg.CallId} result={msg.Result}");
                break;

            case UsageOutput msg:
                if (_jsonOutput)
                {
                    _turnUsage = new JsonUsage
                    {
                        InputTokens = msg.InputTokens,
                        OutputTokens = msg.OutputTokens,
                        TotalTokens = msg.TotalTokens,
                        CachedInputTokens = msg.CachedInputTokens,
                        ReasoningTokens = msg.ReasoningTokens,
                        PromptMs = msg.PromptMs,
                        PredictedPerSecond = msg.PredictedPerSecond,
                    };
                }
                else
                {
                    // If the turn streamed text deltas, they did NOT end with a newline
                    // (each delta is Console.Write). Force the usage line onto its own
                    // line so downstream parsers (evals, humans) can anchor on ^[usage].
                    if (_receivedTextDeltaInCurrentTurn)
                        Console.WriteLine();
                    Console.WriteLine($"[usage] in={msg.InputTokens} out={msg.OutputTokens} total={msg.TotalTokens} cached={msg.CachedInputTokens} prompt_ms={msg.PromptMs} tok_s={msg.PredictedPerSecond}");
                }
                Log(log, $"USAGE: in={msg.InputTokens} out={msg.OutputTokens} total={msg.TotalTokens} cached={msg.CachedInputTokens} reasoning={msg.ReasoningTokens} context_window={msg.ContextWindowTokens} prompt_ms={msg.PromptMs} predicted_tok_s={msg.PredictedPerSecond}");
                break;

            case ErrorOutput msg:
                _lastErrorKind = msg.Category;
                Console.Error.WriteLine($"[error] {SingleLine(msg.Message)}");
                Log(log, $"ERROR: {msg.Message}");
                if (msg.Cause is not null)
                    Log(log, $"EXCEPTION: {msg.Cause}");
                break;

            case TurnCompleted msg:
            {
                // A failed turn with no ErrorOutput is Unknown, which is infra. Never guess "model".
                HeadlessFailure? failure = msg.Outcome == TurnOutcome.Failed
                    ? HeadlessFailure.FromSession(_lastErrorKind ?? ErrorCategory.Unknown)
                    : null;
                _lastErrorKind = null;

                // An absent field (an earlier daemon) maps to an empty list, so the run ends on this turn.
                // The channel stores the open runs before it prints the waiting line.
                var openRuns = msg.OpenChildRunIds.Select(id => id.Value).ToArray();
                _openRuns = new OpenRunsSnapshot(msg.SessionId.Value, openRuns);

                if (failure is not null)
                    ExitCode = 1;
                CloseJsonTurn(msg.TurnNumber.Value);
                if (!_jsonOutput)
                    Console.WriteLine();

                _receivedTextDeltaInCurrentTurn = false;
                _receivedThinkingDeltaInCurrentTurn = false;

                if (!EndsRun(msg))
                {
                    Log(log, $"TURN_COMPLETED: turn={msg.TurnNumber}");
                    var runs = string.Join(",", openRuns);
                    if (!_jsonOutput)
                        Console.WriteLine($"[subagent:waiting] {openRuns.Length} open: {runs}");
                    Log(log, $"WAITING_FOR_CHILDREN: count={openRuns.Length} runs={runs}");
                    break;
                }

                if (_jsonOutput)
                    WriteJsonEnvelope(failure);

                if (failure is { } failed)
                {
                    Console.Error.WriteLine(failed.ToToken());
                    _openRuns.WriteLine();
                    Log(log, $"TURN_FAILED: category={failed.CategoryName} kind={failed.Kind}");
                }

                Log(log, $"TURN_COMPLETED: turn={msg.TurnNumber}");
                EndRun(log, "SESSION_ENDED");
                break;
            }

            case SessionDeactivated msg:
            {
                // The daemon stops this session, for example during a restart. No more turn will come, so
                // waiting would hang. The run fails loudly and writes no envelope.
                ExitCode = 1;
                Console.Error.WriteLine(
                    $"[headless:error] Session {SingleLine(msg.SessionId.Value)} stopped before the run ended.");
                Console.Error.WriteLine(HeadlessFailure.Channel.ToToken());
                _openRuns.WriteLine();
                WriteFailureLog("DEACTIVATED", $"Session {msg.SessionId.Value} stopped before the run ended.");
                EndRun(log, "SESSION_DEACTIVATED");
                break;
            }

            // Headless output cannot answer a prompt. It prints the prompt and its close, so the reader sees why the run
            // waits (#2541). The call ID of a child prompt holds the run ID, so no line prints it (design P3).
            case ToolInteractionRequest msg:
                _shownPrompts.Add(msg.CallId);
                if (!_jsonOutput)
                    Console.WriteLine($"[approval:waiting] {msg.ToolName}: {SingleLine(msg.DisplayText)}");
                if (!_jsonOutput && !string.IsNullOrEmpty(msg.Notes))
                    Console.WriteLine($"[approval:note] {SingleLine(msg.Notes)}");
                Log(log, $"APPROVAL_WAITING: tool={msg.ToolName}");
                break;

            // A close of a prompt that this run never showed is not news to the reader. A new subscriber also gets the
            // restart closes of the session at its join.
            case ToolInteractionClosed msg when _shownPrompts.Remove(msg.CallId):
                var closeText = ChildApprovalPrompt.CloseText(msg.Reason);
                if (!_jsonOutput)
                    Console.WriteLine($"[approval:closed] {closeText}");
                Log(log, $"APPROVAL_CLOSED: {closeText}");
                break;

            case FileOutput msg:
                if (!_jsonOutput)
                    Console.WriteLine($"[file] {msg.FileName} \u2192 {msg.FilePath}");
                Log(log, $"FILE: name={msg.FileName} path={msg.FilePath} mime={msg.MimeType}");
                break;

            case SubAgentOutput msg:
                if (msg.Phase == Netclaw.Actors.SubAgents.SubAgentPhase.Started)
                {
                    if (!_jsonOutput)
                        Console.WriteLine($"[subagent:start] {msg.AgentName} ({msg.ToolCount} tools)");
                    Log(log, $"SUBAGENT_START: name={msg.AgentName} tools={msg.ToolCount}");
                }
                else
                {
                    var status = msg.Outcome.ToString().ToLowerInvariant();
                    var reason = msg.OutcomeReason is { } outcomeReason ? $", reason={outcomeReason.Value}" : string.Empty;
                    if (!_jsonOutput)
                        Console.WriteLine($"[subagent:done] {msg.AgentName} ({status}, {msg.Duration.TotalSeconds:F1}s{reason})");
                    Log(log, $"SUBAGENT_DONE: name={msg.AgentName} success={msg.Success} outcome={status}{reason} duration={msg.Duration.TotalSeconds:F1}s");
                }
                break;

            case CompactionOutput msg:
                if (!_jsonOutput)
                    Console.WriteLine($"[compaction] {msg.MessagesBefore} \u2192 {msg.MessagesAfter} messages (keep={msg.KeepCountUsed}, context={msg.PreCompactionInputTokens}/{msg.ContextWindowTokens} tokens)");
                Log(log, $"COMPACTION: before={msg.MessagesBefore} after={msg.MessagesAfter} tool_results_cleared={msg.ToolResultsCleared} summarized={msg.Summarized} context_window={msg.ContextWindowTokens} input_tokens={msg.PreCompactionInputTokens} keep_count={msg.KeepCountUsed}");
                break;
        }
    }

    /// <summary>
    /// Whether this turn ends the run: a failed turn always does, and a turn that does not fail does when no
    /// child run is open. An absent field from an earlier daemon maps to an empty list, so it ends the run.
    /// </summary>
    // <headless-stop-rule>
    internal static bool EndsRun(TurnCompleted turn)
    {
        if (turn.Outcome == TurnOutcome.Failed)
            return true;

        return !turn.OpenChildRunIds.Any();
    }
    // </headless-stop-rule>

    private void EndRun(StreamWriter? log, string record)
    {
        Log(log, record);
        _ended = true;
        _runEnded.TrySetResult();
    }

    // Error text comes from tools, providers, and models. A line break in it would start a new stderr line,
    // and a forged "[turn:failed]" line would then reach a harness that takes the first match. Only the
    // token writer may start a line with the token.
    private static string SingleLine(string text)
        => LineBreaks.Replace(text, " ");

    private static readonly Regex LineBreaks = new("[\r\n\u0085\u2028\u2029]+", RegexOptions.Compiled);

    // Moves the finished turn into the JSON record and clears the buffers for the next turn.
    private void CloseJsonTurn(int turnNumber)
    {
        if (!_jsonOutput)
            return;

        _turns.Add(new JsonTurn
        {
            TurnNumber = turnNumber,
            Text = _responseBuffer.ToString(),
            ToolCalls = _turnToolCalls.Count > 0 ? [.. _turnToolCalls] : null,
            Usage = _turnUsage
        });
        _responseBuffer.Clear();
        _turnToolCalls.Clear();
        _turnUsage = null;
    }

    private void WriteJsonEnvelope(HeadlessFailure? failure)
    {
        // Client-side timing
        var now = Stopwatch.GetTimestamp();
        double? ttftMs = _firstDeltaTicks > 0 && _promptSentTicks > 0
            ? Stopwatch.GetElapsedTime(_promptSentTicks, _firstDeltaTicks).TotalMilliseconds
            : null;
        double? totalMs = _promptSentTicks > 0
            ? Stopwatch.GetElapsedTime(_promptSentTicks, now).TotalMilliseconds
            : null;

        var allToolCalls = _turns.SelectMany(turn => turn.ToolCalls ?? []).ToList();
        var envelope = new JsonEnvelope
        {
            SessionId = _resolvedSessionId!,
            // The last turn holds the answer. Earlier turns hold the start of a child run.
            Response = _turns[^1].Text,
            TurnResponses = _turns,
            ToolCalls = allToolCalls.Count > 0 ? allToolCalls : null,
            Usage = JsonUsage.Sum(_turns.Select(turn => turn.Usage)),
            TtftMs = ttftMs.HasValue ? Math.Round(ttftMs.Value, 1) : null,
            TotalMs = totalMs.HasValue ? Math.Round(totalMs.Value, 1) : null,
            ErrorCategory = failure?.CategoryName,
            ErrorKind = failure?.Kind,
        };

        Console.WriteLine(JsonSerializer.Serialize(envelope, JsonDefaults.CliOutput));
    }

    // Two threads write the session log. The client pump thread runs the output and the connection callbacks
    // (DaemonClient.EventPumpAsync reads one channel for both). The run thread writes the two start records
    // while SessionJoined and the first outputs already arrive. A StreamWriter is not thread safe, so an
    // unlocked write can drop a line. The run thread closes the writer under the same lock.
    private void Log(StreamWriter? log, string message)
    {
        if (log is null)
            return;

        lock (log)
        {
            if (_logClosed)
                return;
            log.WriteLine($"[{_timeProvider.GetUtcNow():o}] {message}");
        }
    }

    private void WriteFailureLog(string kind, string detail)
    {
        try
        {
            _paths.EnsureDirectoriesExist();
            var path = Path.Combine(_paths.LogsDirectory, "headless-errors.log");
            File.AppendAllText(path,
                $"[{_timeProvider.GetUtcNow():o}] {kind}: {detail}\n");
        }
        catch (Exception logEx)
        {
            Console.Error.WriteLine($"[headless:error] Failed to write failure log: {SingleLine(logEx.Message)}");
        }
    }

    // ── JSON output types ──

    private sealed class JsonEnvelope
    {
        public required string SessionId { get; init; }
        public required string Response { get; init; }
        public required List<JsonTurn> TurnResponses { get; init; }
        public List<JsonToolCall>? ToolCalls { get; init; }
        public JsonUsage? Usage { get; init; }
        public double? TtftMs { get; init; }
        public double? TotalMs { get; init; }

        // Set only for a failed turn. The eval harness reads these two keys.
        public string? ErrorCategory { get; init; }
        public string? ErrorKind { get; init; }
    }

    private sealed class JsonTurn
    {
        public required int TurnNumber { get; init; }
        public required string Text { get; init; }
        public List<JsonToolCall>? ToolCalls { get; init; }
        public JsonUsage? Usage { get; init; }
    }

    private sealed class JsonToolCall
    {
        public required string CallId { get; init; }
        public required string ToolName { get; init; }
        public string? ArgumentsJson { get; init; }
    }

    private sealed class JsonUsage
    {
        public long? InputTokens { get; init; }
        public long? OutputTokens { get; init; }
        public long? TotalTokens { get; init; }
        public long? CachedInputTokens { get; init; }
        public long? ReasoningTokens { get; init; }
        public double? PromptMs { get; init; }
        public double? PredictedPerSecond { get; init; }

        // The token counts add up over the turns. A rate does not add up, so the last turn that has a usage
        // record decides promptMs and predictedPerSecond. A run with one turn gives that turn's usage.
        public static JsonUsage? Sum(IEnumerable<JsonUsage?> perTurn)
        {
            var turns = perTurn.OfType<JsonUsage>().ToArray();
            if (turns.Length == 0)
                return null;

            return new JsonUsage
            {
                InputTokens = SumOf(turns, usage => usage.InputTokens),
                OutputTokens = SumOf(turns, usage => usage.OutputTokens),
                TotalTokens = SumOf(turns, usage => usage.TotalTokens),
                CachedInputTokens = SumOf(turns, usage => usage.CachedInputTokens),
                ReasoningTokens = SumOf(turns, usage => usage.ReasoningTokens),
                PromptMs = turns[^1].PromptMs,
                PredictedPerSecond = turns[^1].PredictedPerSecond,
            };
        }

        private static long? SumOf(JsonUsage[] turns, Func<JsonUsage, long?> field)
        {
            var reported = turns.Select(field).Where(value => value.HasValue).ToArray();
            return reported.Length == 0 ? null : reported.Sum(value => value!.Value);
        }
    }

    // The open runs of the most recent completed turn, with the session that owns them.
    private readonly record struct OpenRunsSnapshot(string SessionId, IReadOnlyList<string> RunIds)
    {
        public static readonly OpenRunsSnapshot None = new(string.Empty, []);

        // One line after the [turn:failed] line, on every failure or stop path that knows an open run. The
        // session owns the runs. The operator resumes it with `netclaw chat --resume <session>`.
        public void WriteLine()
        {
            if (RunIds.Count == 0)
                return;
            Console.Error.WriteLine(
                $"[headless:open-runs] session={SingleLine(SessionId)} runs={SingleLine(string.Join(",", RunIds))}");
        }
    }
}

/// <summary>
/// Why a headless run failed, for a script that reads stderr, the JSON envelope, or the session log.
/// <see cref="Category"/> is the coarse verdict. <see cref="Kind"/> is the finer reason.
/// </summary>
/// <remarks>
/// Only <see cref="HeadlessFailureCategory.Model"/> blames the model. A failure that nobody classified
/// is <see cref="HeadlessFailureCategory.Infra"/>: an unknown failure is no evidence about the model.
/// </remarks>
internal readonly record struct HeadlessFailure(HeadlessFailureCategory Category, string Kind)
{
    /// <summary>The daemon was unreachable, rejected the client, or the prompt exceeded the hub frame limit.</summary>
    public static readonly HeadlessFailure Channel = new(HeadlessFailureCategory.Infra, "ChannelFailure");

    /// <summary>The operator cancelled the run (SIGINT or SIGTERM).</summary>
    public static readonly HeadlessFailure Cancelled = new(HeadlessFailureCategory.Infra, "Cancelled");

    public string CategoryName => Category == HeadlessFailureCategory.Model ? "model" : "infra";

    /// <summary>The stderr line that the eval harness parses. Stdout never carries it.</summary>
    public string ToToken() => $"[turn:failed] category={CategoryName} kind={Kind}";

    /// <summary>
    /// Maps the category of the failing <see cref="ErrorOutput"/>. A value with no name is unclassified, so
    /// it is <c>infra</c> with kind <c>Unknown</c>.
    /// </summary>
    /// <remarks>
    /// The compiler cannot check this switch for a missing member. Without a discard arm it raises CS8524
    /// for values that have no name, and the build treats warnings as errors. So the guard above the switch
    /// handles unnamed values, and the last arm handles a member that someone adds later. That member maps
    /// to <c>infra</c>, the safe side. A new member that should blame the model needs an arm of its own.
    /// </remarks>
    public static HeadlessFailure FromSession(ErrorCategory category)
    {
        if (!Enum.IsDefined(category))
            return new(HeadlessFailureCategory.Infra, nameof(ErrorCategory.Unknown));

        var verdict = category switch
        {
            ErrorCategory.ModelBehavior => HeadlessFailureCategory.Model,
            ErrorCategory.ProviderFailure
                or ErrorCategory.StreamFailure
                or ErrorCategory.Timeout
                or ErrorCategory.ToolFailure
                or ErrorCategory.InputCompatibility
                or ErrorCategory.Unknown => HeadlessFailureCategory.Infra,
            _ => HeadlessFailureCategory.Infra
        };
        return new(verdict, category.ToString());
    }
}

// Infra is the zero value on purpose. A default(HeadlessFailure) must never blame the model.
internal enum HeadlessFailureCategory
{
    Infra = 0,
    Model = 1
}
