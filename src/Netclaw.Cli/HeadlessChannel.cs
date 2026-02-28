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
/// Headless channel for single-prompt mode (<c>-p</c> / <c>--prompt</c>).
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
    private readonly ILogger<HeadlessChannel> _logger;

    private bool _isConnected;
    private bool _receivedTextDeltaInCurrentTurn;
    private bool _receivedThinkingDeltaInCurrentTurn;

    public string ChannelType => "headless";
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
        string prompt,
        ILogger<HeadlessChannel> logger)
    {
        _daemonClient = daemonClient;
        _paths = paths;
        _lifetime = lifetime;
        _timeProvider = timeProvider;
        _prompt = prompt;
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
        try
        {
            var sessionId = new SessionId($"headless/{Guid.NewGuid():N}");

            // Set up session log file
            _paths.EnsureDirectoriesExist();
            var logFileName = $"{sessionId.Value.Replace("/", "-")}.log";
            var logPath = Path.Combine(_paths.LogsDirectory, logFileName);
            await using var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };

            logWriter.WriteLine($"[{_timeProvider.GetUtcNow():o}] Headless session started: {sessionId}");
            logWriter.WriteLine($"[{_timeProvider.GetUtcNow():o}] PROMPT: {_prompt}");

            var turnCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var connectionSubscription = _daemonClient.ConnectionEvents.Subscribe(evt =>
            {
                Log(logWriter, $"CONNECTION: {evt.Message}");
            });

            using var subscription = _daemonClient.SessionOutput.Subscribe(output =>
            {
                HandleOutput(output, logWriter);
                if (output is TurnCompleted)
                    turnCompleted.TrySetResult();
            });

            await _daemonClient.ConnectAsync(stopping);
            _isConnected = true;

            sessionId = new SessionId(await _daemonClient.CreateSessionAsync(ChannelType, stopping));

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
    }

    private void HandleOutput(SessionOutput output, StreamWriter log)
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

                Console.WriteLine(msg.Text);
                Log(log, $"ASSISTANT: {msg.Text}");
                break;

            case TextDeltaOutput msg:
                _receivedTextDeltaInCurrentTurn = true;
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
                Console.WriteLine($"[tool:call] {msg.ToolName}({msg.ArgumentsJson ?? ""})");
                Log(log, $"TOOL_CALL: {msg.ToolName} call_id={msg.CallId} args={msg.ArgumentsJson ?? "{}"}");
                break;

            case ToolResultOutput msg:
                Console.WriteLine($"[tool:result] {msg.ToolName} \u2192 {msg.Result}");
                Log(log, $"TOOL_RESULT: {msg.ToolName} call_id={msg.CallId} result={msg.Result}");
                break;

            case UsageOutput msg:
                Console.WriteLine($"[usage] in={msg.InputTokens} out={msg.OutputTokens} total={msg.TotalTokens}");
                Log(log, $"USAGE: in={msg.InputTokens} out={msg.OutputTokens} total={msg.TotalTokens} cached={msg.CachedInputTokens} reasoning={msg.ReasoningTokens} context_window={msg.ContextWindowTokens}");
                break;

            case ErrorOutput msg:
                Console.Error.WriteLine($"[error] {msg.Message}");
                Log(log, $"ERROR: {msg.Message}");
                if (msg.Cause is not null)
                    Log(log, $"EXCEPTION: {msg.Cause}");
                break;

            case TurnCompleted msg:
                Console.WriteLine();
                Log(log, $"TURN_COMPLETED: turn={msg.TurnNumber}");
                Log(log, "SESSION_ENDED");
                _receivedTextDeltaInCurrentTurn = false;
                _receivedThinkingDeltaInCurrentTurn = false;
                break;

            case FileOutput msg:
                Console.WriteLine($"[file] {msg.FileName} \u2192 {msg.FilePath}");
                Log(log, $"FILE: name={msg.FileName} path={msg.FilePath} mime={msg.MimeType}");
                break;

            case CompactionOutput msg:
                Console.WriteLine($"[compaction] {msg.MessagesBefore} \u2192 {msg.MessagesAfter} messages");
                Log(log, $"COMPACTION: before={msg.MessagesBefore} after={msg.MessagesAfter} tool_results_cleared={msg.ToolResultsCleared} summarized={msg.Summarized}");
                break;
        }
    }

    private void Log(StreamWriter log, string message)
    {
        log.WriteLine($"[{_timeProvider.GetUtcNow():o}] {message}");
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
}
