using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;

namespace Netclaw.App;

/// <summary>
/// Interactive console channel. Reads user input from stdin and renders
/// session output to stdout with color formatting.
///
/// Creates a single session for the channel's lifetime using a
/// <see cref="SessionPipeline"/> for stream-based communication.
///
/// All session activity is logged to ~/.netclaw/logs/{sessionId}.log.
/// Console output is reserved exclusively for the chat UI.
/// </summary>
public sealed class ConsoleChannel : IChannel
{
    private readonly SessionPipeline _pipeline;
    private readonly ActorSystem _system;
    private readonly NetclawPaths _paths;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConsoleChannel> _logger;

    private CancellationTokenRegistration _shutdownRegistration;
    private MaterializedSession? _session;

    public string ChannelType => "console";
    public string DisplayName => "Console Chat";

    public ChannelHealth GetHealth() => _session is not null
        ? new ChannelHealth(ChannelHealthStatus.Healthy)
        : new ChannelHealth(ChannelHealthStatus.Disconnected, "No active session");

    public ConsoleChannel(
        SessionPipeline pipeline,
        ActorSystem system,
        NetclawPaths paths,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider,
        ILogger<ConsoleChannel> logger)
    {
        _pipeline = pipeline;
        _system = system;
        _paths = paths;
        _lifetime = lifetime;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdownRegistration = _lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(() => RunChatLoopAsync(_lifetime.ApplicationStopping), CancellationToken.None);
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_session is not null)
            await _session.DisposeAsync();
        _shutdownRegistration.Dispose();
    }

    private async Task RunChatLoopAsync(CancellationToken stopping)
    {
        try
        {
            var sessionId = new SessionId($"console/{Guid.NewGuid():N}");

            // Set up session log file
            _paths.EnsureDirectoriesExist();
            var logFileName = $"{sessionId.Value.Replace("/", "-")}.log";
            var logPath = Path.Combine(_paths.LogsDirectory, logFileName);
            var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };

            logWriter.WriteLine($"[{_timeProvider.GetUtcNow():o}] Session started: {sessionId}");

            // Create session pipeline
            _session = await _pipeline.CreateAsync(sessionId, new SessionPipelineOptions
            {
                ChannelType = ChannelType
            }, stopping);

            // Materialize output stream → console rendering + disk logging
            _session.Output
                .To(Sink.ForEach<SessionOutput>(output => RenderOutput(output, logWriter)))
                .Run(_system);

            // Materialize input with queue for imperative push from readline
            var inputQueue = Source.Queue<ChannelInput>(16, OverflowStrategy.Backpressure)
                .ToMaterialized(_session.Input, Keep.Left)
                .Run(_system);

            _logger.LogInformation("Session started: {SessionId} (log: {LogPath})", sessionId, logPath);
            Console.WriteLine();
            Console.WriteLine($"Netclaw console chat (log: {logPath})");
            Console.WriteLine("Type 'exit' to quit.");
            Console.WriteLine("──────────────────────────────────────────");
            Console.WriteLine();

            while (!stopping.IsCancellationRequested)
            {
                Console.Write("You> ");
                var input = Console.ReadLine();

                if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                {
                    logWriter.WriteLine($"[{_timeProvider.GetUtcNow():o}] User exited chat");
                    _lifetime.StopApplication();
                    break;
                }

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                logWriter.WriteLine($"[{_timeProvider.GetUtcNow():o}] USER: {input}");

                await inputQueue.OfferAsync(new ChannelInput
                {
                    SenderId = "local-user",
                    Contents = [new TextContent(input)],
                    ReceivedAt = _timeProvider.GetUtcNow()
                });
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Console chat loop cancelled (shutdown)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Console chat loop failed");
            _lifetime.StopApplication();
        }
    }

    private static void RenderOutput(SessionOutput output, StreamWriter log)
    {
        switch (output)
        {
            case SessionJoined msg:
                Log(log, $"SESSION_JOINED turn_count={msg.TurnCount} title={msg.Title ?? "(none)"}");
                break;

            case TextOutput msg:
                Console.WriteLine();
                Console.WriteLine($"Netclaw> {msg.Text}");
                Console.WriteLine();
                Log(log, $"ASSISTANT: {msg.Text}");
                break;

            case ThinkingOutput msg:
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  [thinking] {msg.Text}");
                Console.ResetColor();
                Log(log, $"THINKING: {msg.Text}");
                break;

            case ToolCallOutput msg:
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  [tool] {msg.ToolName}({msg.ArgumentsJson ?? ""})");
                Console.ResetColor();
                Log(log, $"TOOL_CALL: {msg.ToolName} call_id={msg.CallId} args={msg.ArgumentsJson ?? "{}"}");
                break;

            case ToolResultOutput msg:
                Log(log, $"TOOL_RESULT: {msg.ToolName} call_id={msg.CallId} result={msg.Result}");
                break;

            case UsageOutput msg:
                var usage = msg.UsagePercent.HasValue
                    ? $" ({msg.UsagePercent.Value:P0} context)"
                    : "";
                Log(log, $"USAGE: in={msg.InputTokens} out={msg.OutputTokens} total={msg.TotalTokens} cached={msg.CachedInputTokens} reasoning={msg.ReasoningTokens} context_window={msg.ContextWindowTokens}{usage}");
                break;

            case ErrorOutput msg:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [error] {msg.Message}");
                Console.ResetColor();
                Log(log, $"ERROR: {msg.Message}");
                if (msg.Cause is not null)
                    Log(log, $"EXCEPTION: {msg.Cause}");
                break;

            case TurnCompleted msg:
                Log(log, $"TURN_COMPLETED: turn={msg.TurnNumber}");
                break;

            case CompactionOutput msg:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [compaction] {msg.MessagesBefore} → {msg.MessagesAfter} messages");
                Console.ResetColor();
                Log(log, $"COMPACTION: before={msg.MessagesBefore} after={msg.MessagesAfter} tool_results_cleared={msg.ToolResultsCleared} summarized={msg.Summarized}");
                break;
        }
    }

    private static void Log(StreamWriter log, string message)
    {
        log.WriteLine($"[{DateTimeOffset.UtcNow:o}] {message}");
    }
}
