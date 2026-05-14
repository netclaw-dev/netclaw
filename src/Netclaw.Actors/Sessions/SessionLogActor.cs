// -----------------------------------------------------------------------
// <copyright file="SessionLogActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;

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
/// tree deliberately separate from the agent-accessible session working
/// directory so the LLM cannot read its own audit trail via the file_read tool.
/// </summary>
public sealed class SessionLogActor : ReceiveActor
{
    private readonly SessionId _sessionId;
    private readonly string _sessionLogsBasePath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly ILoggingAdapter _log = Context.GetLogger();

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
        Context.SetReceiveTimeout(_idleTimeout);
    }

    private void OnUserMessage(SendUserMessage msg)
    {
        try
        {
            var mediaNote = msg.MediaReferences.Count > 0
                ? $" (+{msg.MediaReferences.Count} media)"
                : string.Empty;
            SessionLogFile.AppendLine(_sessionId, _sessionLogsBasePath,
                $"[{_timeProvider.GetUtcNow():o}] User: {TextTruncation.EllipsisAppend(msg.Content, 1000)}{mediaNote}");
        }
        catch (Exception ex)
        {
            // AppendLine retries transient IO failures internally; reaching this
            // catch means the audit line was lost. Audit-trail loss is a real
            // production fault — log loudly, not at Debug.
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
                ToolResultOutput toolResult => $"Tool result: {toolResult.ToolName} (call={toolResult.CallId}) → {TextTruncation.EllipsisAppend(toolResult.Result, 1000)}",
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
                SessionLogFile.AppendLine(_sessionId, _sessionLogsBasePath,
                    $"[{_timeProvider.GetUtcNow():o}] {line}");
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
            SessionLogFile.AppendLine(_sessionId, _sessionLogsBasePath, diagnostic.Line);
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
