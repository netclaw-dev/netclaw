using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Per-session child actor that owns the log file lifecycle.
/// Created by <see cref="LlmSessionActor"/> when a session logs directory is configured.
/// Not persistent — log files are best-effort observability.
/// The actor opens the log file in <see cref="PreStart"/> and disposes it in <see cref="PostStop"/>,
/// ensuring the file handle is properly released when the session actor passivates.
/// </summary>
public sealed class SessionLogActor : ReceiveActor
{
    private readonly SessionId _sessionId;
    private readonly string _logDirectory;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private StreamWriter? _writer;

    public static Props CreateProps(SessionId sessionId, string logDirectory) =>
        Props.Create(() => new SessionLogActor(sessionId, logDirectory));

    public SessionLogActor(SessionId sessionId, string logDirectory)
    {
        _sessionId = sessionId;
        _logDirectory = logDirectory;

        Receive<SendUserMessage>(OnUserMessage);
        Receive<SessionOutput>(OnOutput);
    }

    protected override void PreStart()
    {
        try
        {
            var sanitized = SessionDirectoryHelper.SanitizeSessionId(_sessionId.Value);
            var logPath = Path.Combine(_logDirectory, $"{sanitized}.log");
            Directory.CreateDirectory(_logDirectory);
            _writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
            _writer.WriteLine($"[{DateTimeOffset.UtcNow:o}] Session log started: {_sessionId.Value}");
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
            _writer.WriteLine($"[{DateTimeOffset.UtcNow:o}] User: {Truncate(msg.Content, 200)}{mediaNote}");
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
                TextOutput text => $"Assistant: {Truncate(text.Text, 200)}",
                ToolCallOutput toolCall => $"Tool call: {toolCall.ToolName} (call={toolCall.CallId})",
                ToolResultOutput toolResult => $"Tool result: {toolResult.ToolName} (call={toolResult.CallId}) → {Truncate(toolResult.Result, 200)}",
                TurnCompleted tc => $"Turn {tc.TurnNumber} completed",
                SessionTitleOutput title => $"Title set: {title.Title}",
                CompactionOutput compaction =>
                    $"Compaction: {compaction.MessagesBefore} → {compaction.MessagesAfter} messages " +
                    $"(keep={compaction.KeepCountUsed}, context={compaction.PreCompactionInputTokens}/{compaction.ContextWindowTokens} tokens)",
                ErrorOutput error => $"Error: {error.Message}",
                FileOutput file => $"File: {file.FileName} ({file.MimeType})",
                _ => null
            };

            if (line is not null)
            {
                _writer.WriteLine($"[{DateTimeOffset.UtcNow:o}] {line}");
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to write session log entry for {SessionId}", _sessionId.Value);
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");
}
