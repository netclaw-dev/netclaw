// -----------------------------------------------------------------------
// <copyright file="SessionLogActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Security;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Per-session writer actor for the canonical <c>session.log</c> file.
/// Created lazily by <see cref="SessionLogDispatcher"/> on first message and
/// stopped via <see cref="ReceiveTimeout"/> after idle. Receives session
/// audit messages (<see cref="SendUserMessage"/>, <see cref="SessionOutput"/>)
/// and pre-formatted diagnostic lines (<see cref="SessionLogDiagnostic"/>)
/// from the MEL logger provider, and is the sole writer to the file path
/// computed by <see cref="SessionLogFile"/>.
///
/// Log files live at <c>{sessionLogsBase}/{sanitized_id}/session.log</c> — a
/// tree deliberately separate from the agent-visible session working
/// directory so the LLM cannot read its own audit trail via the file_read tool.
///
/// File handle lifecycle:
/// - Open once in <see cref="PreStart"/> with append mode + read-share.
/// - AutoFlush enabled — each WriteLine flushes immediately.
/// - Close/dispose in <see cref="PostStop"/> — no retry loop needed since the
///   handle is kept open and single-writer is enforced by the actor mailbox.
/// </summary>
public sealed class SessionLogActor : ReceiveActor
{
    private readonly SessionId _sessionId;
    private readonly string _sessionLogsBasePath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private StreamWriter? _writer;

    public static Props CreateProps(SessionId sessionId, string sessionLogsBasePath, TimeProvider timeProvider, TimeSpan? idleTimeout = null) =>
        Props.Create(() => new SessionLogActor(sessionId, sessionLogsBasePath, timeProvider, idleTimeout ?? TimeSpan.FromMinutes(10)));

    public SessionLogActor(SessionId sessionId, string sessionLogsBasePath, TimeProvider timeProvider, TimeSpan idleTimeout)
    {
        _sessionId = sessionId;
        _sessionLogsBasePath = sessionLogsBasePath;
        _timeProvider = timeProvider;
        _idleTimeout = idleTimeout;

        Receive<SendUserMessage>(OnUserMessage);
        Receive<SessionOutput>(OnOutput);
        Receive<SessionLogDiagnostic>(OnDiagnostic);
        Receive<ReceiveTimeout>(_ => Context.Stop(Self));
    }

    protected override void PreStart()
    {
        base.PreStart();
        Context.SetReceiveTimeout(_idleTimeout);

        var logPath = SessionLogFile.GetLogPath(_sessionId, _sessionLogsBasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        // Open once: append mode + read-share so concurrent readers (tail, diagnostics, tests)
        // can open the file without conflicting with our writer handle.
        // FileShare.Delete also lets log rotation / Directory.Delete proceed on Windows.
        var stream = new FileStream(
            logPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: false);

        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    protected override void PostStop()
    {
        _writer?.Dispose();
        base.PostStop();
    }

    private void OnUserMessage(SendUserMessage msg)
    {
        try
        {
            var mediaNote = msg.MediaReferences.Count > 0
                ? $" (+{msg.MediaReferences.Count} media)"
                : string.Empty;
            var line = $"[{_timeProvider.GetUtcNow():o}] User: {TextTruncation.EllipsisAppend(msg.Content, 1000)}{mediaNote}";

            _writer?.WriteLine(line);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Dropped user message audit line for {SessionId}", _sessionId.Value);
        }
    }

    private void OnOutput(SessionOutput output)
    {
        try
        {
            var line = output switch
            {
                TextOutput text => $"Assistant: {TextTruncation.EllipsisAppend(text.Text, 1000)}",
                ToolCallOutput toolCall => FormatToolCall(toolCall),
                ToolResultOutput toolResult => $"Tool result: {toolResult.ToolName} (call={toolResult.CallId}) → {TextTruncation.EllipsisAppend(SecretOutputRedactor.Redact(toolResult.Result), 1000)}",
                ThinkingOutput thinking => $"Thinking: {TextTruncation.EllipsisAppend(thinking.Text, 1000)}",
                ThinkingDeltaOutput thinkingDelta => $"Thinking delta: {TextTruncation.EllipsisAppend(thinkingDelta.Delta, 1000)}",
                UsageOutput usage => $"Usage: in={usage.InputTokens} out={usage.OutputTokens} cached={usage.CachedInputTokens} reasoning={usage.ReasoningTokens} context={usage.UsagePercent:P0}",
                TurnCompleted tc => $"Turn {tc.TurnNumber} {tc.Outcome.ToString().ToLowerInvariant()}",
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
                _writer?.WriteLine($"[{_timeProvider.GetUtcNow():o}] {line}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Dropped session log audit line for {SessionId}", _sessionId.Value);
        }
    }

    private void OnDiagnostic(SessionLogDiagnostic diagnostic)
    {
        try
        {
            _writer?.WriteLine(diagnostic.Line);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Dropped diagnostic audit line for {SessionId}", _sessionId.Value);
        }
    }

    private static string FormatToolCall(ToolCallOutput toolCall)
    {
        var args = toolCall.ArgumentsJson is not null
            ? $" args={TextTruncation.EllipsisAppend(toolCall.ArgumentsJson, 1000)}"
            : string.Empty;
        return $"Tool call: {toolCall.ToolName} (call={toolCall.CallId}){args}";
    }
}
