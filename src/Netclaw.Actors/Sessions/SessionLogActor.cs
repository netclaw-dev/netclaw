using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Per-session child actor that owns the log file lifecycle.
/// Created by <see cref="LlmSessionActor"/> when a sessions base directory is configured.
/// Not persistent — log files are best-effort observability.
/// The actor opens the log file in <see cref="PreStart"/> and disposes it in <see cref="PostStop"/>,
/// ensuring the file handle is properly released when the session actor passivates.
///
/// Log files are stored inside the session's own directory:
/// <c>{sessionsBase}/{sanitized_id}/logs/{timestamp}.log</c>
/// Multiple log files in the same directory indicate passivation/rehydration cycles.
/// </summary>
public sealed class SessionLogActor : ReceiveActor
{
    private readonly SessionId _sessionId;
    private readonly string _sessionsBasePath;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private StreamWriter? _writer;

    public static Props CreateProps(SessionId sessionId, string sessionsBasePath, TimeProvider timeProvider) =>
        Props.Create(() => new SessionLogActor(sessionId, sessionsBasePath, timeProvider));

    public SessionLogActor(SessionId sessionId, string sessionsBasePath, TimeProvider timeProvider)
    {
        _sessionId = sessionId;
        _sessionsBasePath = sessionsBasePath;
        _timeProvider = timeProvider;

        Receive<SendUserMessage>(OnUserMessage);
        Receive<SessionOutput>(OnOutput);
    }

    /// <summary>
    /// Computes the logs directory for this session:
    /// <c>{sessionsBase}/{sanitized_id}/logs/</c>
    /// </summary>
    internal static string GetSessionLogsDirectory(SessionId sessionId, string sessionsBasePath)
    {
        var sanitized = SessionDirectoryHelper.SanitizeSessionId(sessionId.Value);
        return Path.Combine(sessionsBasePath, sanitized, "logs");
    }

    protected override void PreStart()
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var logsDir = GetSessionLogsDirectory(_sessionId, _sessionsBasePath);
            var logPath = Path.Combine(logsDir, $"{now:yyyyMMdd-HHmmss}.log");
            Directory.CreateDirectory(logsDir);
            _writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
            _writer.WriteLine($"[{now:o}] Session log started: {_sessionId.Value}");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to open session log file for {SessionId}", _sessionId.Value);
        }
    }

    protected override void PostStop()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to dispose session log writer for {SessionId}", _sessionId.Value);
        }
    }

    private void OnUserMessage(SendUserMessage msg)
    {
        if (_writer is null) return;

        try
        {
            var mediaNote = msg.MediaReferences.Count > 0
                ? $" (+{msg.MediaReferences.Count} media)"
                : string.Empty;
            _writer.WriteLine($"[{_timeProvider.GetUtcNow():o}] User: {Truncate(msg.Content, 1000)}{mediaNote}");
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to write user message log entry for {SessionId}", _sessionId.Value);
        }
    }

    private void OnOutput(SessionOutput output)
    {
        if (_writer is null) return;

        try
        {
            var line = output switch
            {
                TextOutput text => $"Assistant: {Truncate(text.Text, 1000)}",
                ToolCallOutput toolCall => FormatToolCall(toolCall),
                ToolResultOutput toolResult => $"Tool result: {toolResult.ToolName} (call={toolResult.CallId}) → {Truncate(toolResult.Result, 1000)}",
                ThinkingOutput thinking => $"Thinking: {Truncate(thinking.Text, 1000)}",
                UsageOutput usage => $"Usage: in={usage.InputTokens} out={usage.OutputTokens} cached={usage.CachedInputTokens} reasoning={usage.ReasoningTokens} context={usage.UsagePercent:P0}",
                TurnCompleted tc => $"Turn {tc.TurnNumber} completed",
                SessionTitleOutput title => $"Title set: {title.Title}",
                CompactionOutput compaction =>
                    $"Compaction: {compaction.MessagesBefore} → {compaction.MessagesAfter} messages " +
                    $"(keep={compaction.KeepCountUsed}, context={compaction.PreCompactionInputTokens}/{compaction.ContextWindowTokens} tokens)",
                SubAgentOutput sa when sa.Phase == SubAgentPhase.Started =>
                    $"SubAgent started: {sa.AgentName} (tools={sa.ToolCount})",
                SubAgentOutput sa =>
                    $"SubAgent completed: {sa.AgentName} (success={sa.Success}, duration={sa.Duration.TotalSeconds:F1}s, findings={sa.FindingsCount}, memory={sa.MemoryDecision ?? "n/a"}{(string.IsNullOrWhiteSpace(sa.MemoryDecisionReason) ? string.Empty : $", reason={sa.MemoryDecisionReason}")})",
                ErrorOutput error => $"Error [{error.Category}] (ref: {error.CorrelationId:N}): {error.Message}",
                FileOutput file => $"File: {file.FileName} ({file.MimeType})",
                _ => null
            };

            if (line is not null)
            {
                _writer.WriteLine($"[{_timeProvider.GetUtcNow():o}] {line}");
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to write session log entry for {SessionId}", _sessionId.Value);
        }
    }

    private static string FormatToolCall(ToolCallOutput toolCall)
    {
        var args = toolCall.ArgumentsJson is not null
            ? $" args={Truncate(toolCall.ArgumentsJson, 1000)}"
            : string.Empty;
        return $"Tool call: {toolCall.ToolName} (call={toolCall.CallId}){args}";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");
}
