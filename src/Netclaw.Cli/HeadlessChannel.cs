using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Cli.Daemon;
using R3;

namespace Netclaw.Cli;

/// <summary>
/// Headless channel for single-prompt mode (<c>chat -p</c>).
/// Sends one message to the LLM session, streams all output to stdout,
/// and exits on <see cref="TurnCompleted"/>.
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

    // JSON output accumulation
    private readonly StringBuilder _responseBuffer = new();
    private readonly List<JsonToolCall> _toolCalls = [];
    private JsonUsage? _usage;
    private string? _resolvedSessionId;

    // Client-side timing
    private long _promptSentTicks;
    private long _firstDeltaTicks;

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
        _ = Task.Run(() => RunHeadlessAsync(_lifetime.ApplicationStopping), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _isConnected = false;
        await Task.CompletedTask;
    }

    private async Task RunHeadlessAsync(CancellationToken stopping)
    {
        // Log writer is deferred until we know the session ID (after create/resume).
        // Volatile ensures the subscription callback sees the assigned value across threads.
        StreamWriter? logWriter = null;

        try
        {
            _paths.EnsureDirectoriesExist();

            var turnCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var connectionSubscription = _daemonClient.ConnectionEvents.Subscribe(evt =>
            {
                var lw = Volatile.Read(ref logWriter);
                if (lw is not null)
                    Log(lw, $"CONNECTION: {evt.Message}");
            });

            using var subscription = _daemonClient.SessionOutput.Subscribe(output =>
            {
                HandleOutput(output, Volatile.Read(ref logWriter));
                if (output is TurnCompleted)
                    turnCompleted.TrySetResult();
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

            logWriter!.WriteLine($"[{_timeProvider.GetUtcNow():o}] Headless session started: {sessionId}");
            logWriter.WriteLine($"[{_timeProvider.GetUtcNow():o}] PROMPT: {_prompt}");

            _promptSentTicks = Stopwatch.GetTimestamp();

            await _daemonClient.SendAsync(new Netclaw.Actors.Channels.ChannelInput
            {
                SenderId = "local-user",
                Contents = [new TextContent(_prompt)],
                ReceivedAt = _timeProvider.GetUtcNow()
            }, stopping);

            _logger.LogInformation("Headless session started: {SessionId} (log: {LogPath})", sessionId, logPath);

            await turnCompleted.Task.WaitAsync(stopping);
            _lifetime.StopApplication();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Headless channel cancelled (shutdown)");
            WriteFailureLog("CANCELLED", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Headless channel failed");
            Console.Error.WriteLine($"[headless:error] {ex.Message}");
            WriteFailureLog("FAILED", ex);
            Environment.ExitCode = 1;
            _lifetime.StopApplication();
        }
        finally
        {
            if (logWriter is not null)
                await logWriter.DisposeAsync();
        }
    }

    private void HandleOutput(SessionOutput output, StreamWriter? log)
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
                    _toolCalls.Add(new JsonToolCall
                    {
                        CallId = msg.CallId,
                        ToolName = msg.ToolName,
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
                    _usage = new JsonUsage
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
                Console.Error.WriteLine($"[error] {msg.Message}");
                Log(log, $"ERROR: {msg.Message}");
                if (msg.Cause is not null)
                    Log(log, $"EXCEPTION: {msg.Cause}");
                break;

            case TurnCompleted msg:
                if (_jsonOutput)
                {
                    WriteJsonEnvelope();
                }
                else
                {
                    Console.WriteLine();
                }
                Log(log, $"TURN_COMPLETED: turn={msg.TurnNumber}");
                Log(log, "SESSION_ENDED");
                _receivedTextDeltaInCurrentTurn = false;
                _receivedThinkingDeltaInCurrentTurn = false;
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
                    var status = msg.Success ? "success" : "failed";
                    if (!_jsonOutput)
                        Console.WriteLine($"[subagent:done] {msg.AgentName} ({status}, {msg.Duration.TotalSeconds:F1}s)");
                    Log(log, $"SUBAGENT_DONE: name={msg.AgentName} success={msg.Success} duration={msg.Duration.TotalSeconds:F1}s");
                }
                break;

            case CompactionOutput msg:
                if (!_jsonOutput)
                    Console.WriteLine($"[compaction] {msg.MessagesBefore} \u2192 {msg.MessagesAfter} messages (keep={msg.KeepCountUsed}, context={msg.PreCompactionInputTokens}/{msg.ContextWindowTokens} tokens)");
                Log(log, $"COMPACTION: before={msg.MessagesBefore} after={msg.MessagesAfter} tool_results_cleared={msg.ToolResultsCleared} summarized={msg.Summarized} context_window={msg.ContextWindowTokens} input_tokens={msg.PreCompactionInputTokens} keep_count={msg.KeepCountUsed}");
                break;
        }
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private void WriteJsonEnvelope()
    {
        // Client-side timing
        var now = Stopwatch.GetTimestamp();
        double? ttftMs = _firstDeltaTicks > 0 && _promptSentTicks > 0
            ? Stopwatch.GetElapsedTime(_promptSentTicks, _firstDeltaTicks).TotalMilliseconds
            : null;
        double? totalMs = _promptSentTicks > 0
            ? Stopwatch.GetElapsedTime(_promptSentTicks, now).TotalMilliseconds
            : null;

        var envelope = new JsonEnvelope
        {
            SessionId = _resolvedSessionId!,
            Response = _responseBuffer.ToString(),
            ToolCalls = _toolCalls.Count > 0 ? _toolCalls : null,
            Usage = _usage,
            TtftMs = ttftMs.HasValue ? Math.Round(ttftMs.Value, 1) : null,
            TotalMs = totalMs.HasValue ? Math.Round(totalMs.Value, 1) : null,
        };

        Console.WriteLine(JsonSerializer.Serialize(envelope, s_jsonOptions));
    }

    private void Log(StreamWriter? log, string message)
    {
        log?.WriteLine($"[{_timeProvider.GetUtcNow():o}] {message}");
    }

    private void WriteFailureLog(string kind, Exception ex)
    {
        try
        {
            _paths.EnsureDirectoriesExist();
            var path = Path.Combine(_paths.LogsDirectory, "headless-errors.log");
            File.AppendAllText(path,
                $"[{_timeProvider.GetUtcNow():o}] {kind}: {ex}\n");
        }
        catch (Exception logEx)
        {
            Console.Error.WriteLine($"[headless:error] Failed to write failure log: {logEx.Message}");
        }
    }

    // ── JSON output types ──

    private sealed class JsonEnvelope
    {
        public required string SessionId { get; init; }
        public required string Response { get; init; }
        public List<JsonToolCall>? ToolCalls { get; init; }
        public JsonUsage? Usage { get; init; }
        public double? TtftMs { get; init; }
        public double? TotalMs { get; init; }
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
    }
}
