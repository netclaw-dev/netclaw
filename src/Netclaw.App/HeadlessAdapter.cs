using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Configuration;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.App;

/// <summary>
/// Hosted service that sends a single prompt to the LLM session and streams
/// all output (tool calls, results, text, usage) to stdout, then exits.
///
/// This is the <c>-p</c> / <c>--prompt</c> headless mode — same UX concept
/// as <c>claude -p</c>. Useful for smoke-testing tool discovery and invocation
/// against different models without a human in the loop.
/// </summary>
public sealed class HeadlessAdapter : IHostedService
{
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;
    private readonly ActorSystem _actorSystem;
    private readonly NetclawPaths _paths;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly HeadlessOptions _options;
    private readonly ILogger<HeadlessAdapter> _logger;

    private CancellationTokenRegistration _shutdownRegistration;

    public HeadlessAdapter(
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider,
        ActorSystem actorSystem,
        NetclawPaths paths,
        IHostApplicationLifetime lifetime,
        HeadlessOptions options,
        ILogger<HeadlessAdapter> logger)
    {
        _sessionManagerProvider = sessionManagerProvider;
        _actorSystem = actorSystem;
        _paths = paths;
        _lifetime = lifetime;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdownRegistration = _lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(() => RunHeadlessAsync(_lifetime.ApplicationStopping), CancellationToken.None);
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownRegistration.Dispose();
        return Task.CompletedTask;
    }

    private async Task RunHeadlessAsync(CancellationToken stopping)
    {
        try
        {
            var sessionManager = await _sessionManagerProvider.GetAsync(stopping);
            var sessionId = new SessionId($"headless/{Guid.NewGuid():N}");

            // Set up session log file
            _paths.EnsureDirectoriesExist();
            var logFileName = $"{sessionId.Value.Replace("/", "-")}.log";
            var logPath = Path.Combine(_paths.LogsDirectory, logFileName);
            var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };

            logWriter.WriteLine($"[{DateTimeOffset.UtcNow:o}] Headless session started: {sessionId}");
            logWriter.WriteLine($"[{DateTimeOffset.UtcNow:o}] PROMPT: {_options.Prompt}");

            // Create subscriber actor that writes session output to stdout + log
            var subscriber = _actorSystem.ActorOf(
                Props.Create(() => new HeadlessSubscriberActor(logWriter, _lifetime)),
                $"headless-subscriber-{sessionId.Value.Replace("/", "-")}");

            // Join the session with full output (tool calls, thinking, usage)
            sessionManager.Tell(new JoinSession
            {
                SessionId = sessionId,
                Subscriber = subscriber,
                Filter = OutputFilter.Full
            });

            // Send the single prompt
            sessionManager.Tell(new SendUserMessage
            {
                SessionId = sessionId,
                Content = _options.Prompt
            });

            _logger.LogInformation("Headless session started: {SessionId} (log: {LogPath})", sessionId, logPath);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Headless adapter cancelled (shutdown)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Headless adapter failed");
            _lifetime.StopApplication();
        }
    }
}

/// <summary>
/// Subscriber actor for headless mode. Streams all session output to stdout
/// in a human-readable format and shuts down the application on <see cref="TurnCompleted"/>.
/// </summary>
public sealed class HeadlessSubscriberActor : ReceiveActor
{
    private readonly StreamWriter _log;
    private readonly IHostApplicationLifetime _lifetime;

    public HeadlessSubscriberActor(StreamWriter logWriter, IHostApplicationLifetime lifetime)
    {
        _log = logWriter;
        _lifetime = lifetime;

        Receive<SessionJoined>(msg =>
        {
            Log($"SESSION_JOINED turn_count={msg.TurnCount} title={msg.Title ?? "(none)"}");
        });

        Receive<TextOutput>(msg =>
        {
            Console.WriteLine(msg.Text);
            Log($"ASSISTANT: {msg.Text}");
        });

        Receive<ThinkingOutput>(msg =>
        {
            Console.WriteLine($"[thinking] {msg.Text}");
            Log($"THINKING: {msg.Text}");
        });

        Receive<ToolCallOutput>(msg =>
        {
            Console.WriteLine($"[tool:call] {msg.ToolName}({msg.ArgumentsJson ?? ""})");
            Log($"TOOL_CALL: {msg.ToolName} call_id={msg.CallId} args={msg.ArgumentsJson ?? "{}"}");
        });

        Receive<ToolResultOutput>(msg =>
        {
            Console.WriteLine($"[tool:result] {msg.ToolName} \u2192 {msg.Result}");
            Log($"TOOL_RESULT: {msg.ToolName} call_id={msg.CallId} result={msg.Result}");
        });

        Receive<UsageOutput>(msg =>
        {
            Console.WriteLine($"[usage] in={msg.InputTokens} out={msg.OutputTokens} total={msg.TotalTokens}");
            Log($"USAGE: in={msg.InputTokens} out={msg.OutputTokens} total={msg.TotalTokens} cached={msg.CachedInputTokens} reasoning={msg.ReasoningTokens} context_window={msg.ContextWindowTokens}");
        });

        Receive<ErrorOutput>(msg =>
        {
            Console.Error.WriteLine($"[error] {msg.Message}");
            Log($"ERROR: {msg.Message}");
            if (msg.Cause is not null)
                Log($"EXCEPTION: {msg.Cause}");
        });

        Receive<TurnCompleted>(msg =>
        {
            Log($"TURN_COMPLETED: turn={msg.TurnNumber}");
            _lifetime.StopApplication();
        });

        Receive<CompactionOutput>(msg =>
        {
            Console.WriteLine($"[compaction] {msg.MessagesBefore} \u2192 {msg.MessagesAfter} messages");
            Log($"COMPACTION: before={msg.MessagesBefore} after={msg.MessagesAfter} tool_results_cleared={msg.ToolResultsCleared} summarized={msg.Summarized}");
        });

        Receive<SessionTitleOutput>(_ =>
        {
            // Ignored in headless mode — no UI to update
        });
    }

    private void Log(string message)
    {
        _log.WriteLine($"[{DateTimeOffset.UtcNow:o}] {message}");
    }

    protected override void PostStop()
    {
        Log("SESSION_ENDED");
        _log.Dispose();
        base.PostStop();
    }
}
