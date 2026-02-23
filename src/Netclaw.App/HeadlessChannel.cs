using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Configuration;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;

namespace Netclaw.App;

/// <summary>
/// Headless channel for single-prompt mode (<c>-p</c> / <c>--prompt</c>).
/// Sends one message to the LLM session, streams all output to stdout,
/// and exits on <see cref="TurnCompleted"/>.
/// </summary>
public sealed class HeadlessChannel : IChannel
{
    private readonly SessionPipeline _pipeline;
    private readonly ActorSystem _system;
    private readonly NetclawPaths _paths;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly string _prompt;
    private readonly ILogger<HeadlessChannel> _logger;

    private MaterializedSession? _session;

    public string ChannelType => "headless";
    public string DisplayName => "Headless Prompt";

    public ChannelHealth GetHealth() => _session is not null
        ? new ChannelHealth(ChannelHealthStatus.Healthy)
        : new ChannelHealth(ChannelHealthStatus.Disconnected, "No active session");

    public HeadlessChannel(
        SessionPipeline pipeline,
        ActorSystem system,
        NetclawPaths paths,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider,
        string prompt,
        ILogger<HeadlessChannel> logger)
    {
        _pipeline = pipeline;
        _system = system;
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
        if (_session is not null)
            await _session.DisposeAsync();
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
            var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };

            logWriter.WriteLine($"[{_timeProvider.GetUtcNow():o}] Headless session started: {sessionId}");
            logWriter.WriteLine($"[{_timeProvider.GetUtcNow():o}] PROMPT: {_prompt}");

            // Create session pipeline
            _session = await _pipeline.CreateAsync(sessionId, new SessionPipelineOptions
            {
                ChannelType = ChannelType
            }, stopping);

            // Materialize output stream → console + disk logging, exit on TurnCompleted
            _session.Output
                .To(Sink.ForEach<SessionOutput>(output => HandleOutput(output, logWriter)))
                .Run(_system);

            // Materialize input with queue and send the single prompt
            var inputQueue = Source.Queue<ChannelInput>(16, OverflowStrategy.Backpressure)
                .ToMaterialized(_session.Input, Keep.Left)
                .Run(_system);

            await inputQueue.OfferAsync(new ChannelInput
            {
                SenderId = "local-user",
                Contents = [new TextContent(_prompt)],
                ReceivedAt = _timeProvider.GetUtcNow()
            });

            _logger.LogInformation("Headless session started: {SessionId} (log: {LogPath})", sessionId, logPath);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Headless channel cancelled (shutdown)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Headless channel failed");
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
                Console.WriteLine(msg.Text);
                Log(log, $"ASSISTANT: {msg.Text}");
                break;

            case ThinkingOutput msg:
                Console.WriteLine($"[thinking] {msg.Text}");
                Log(log, $"THINKING: {msg.Text}");
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
                Log(log, $"TURN_COMPLETED: turn={msg.TurnNumber}");
                Log(log, "SESSION_ENDED");
                _lifetime.StopApplication();
                break;

            case CompactionOutput msg:
                Console.WriteLine($"[compaction] {msg.MessagesBefore} \u2192 {msg.MessagesAfter} messages");
                Log(log, $"COMPACTION: before={msg.MessagesBefore} after={msg.MessagesAfter} tool_results_cleared={msg.ToolResultsCleared} summarized={msg.Summarized}");
                break;
        }
    }

    private static void Log(StreamWriter log, string message)
    {
        log.WriteLine($"[{DateTimeOffset.UtcNow:o}] {message}");
    }
}
