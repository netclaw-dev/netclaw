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
/// - The high-volume diagnostic lines are flushed in batches (after a write burst or on a
///   periodic tick), not per line: now that the whole per-session log stream lands here, an
///   fsync per line would dominate. The audit transcript (user/assistant/tool/usage) is
///   flushed immediately instead, so a hard process death cannot drop the audit record's tail.
///   <see cref="FlushTick"/> is <c>INotInfluenceReceiveTimeout</c> so the flush cadence does
///   not keep an idle session alive.
/// - Close/dispose in <see cref="PostStop"/> (which flushes) — no retry loop needed since
///   the handle is kept open and single-writer is enforced by the actor mailbox.
/// </summary>
public sealed class SessionLogActor : ReceiveActor, IWithTimers
{
    // Flush after this many buffered writes (bounds the buffer under a burst) or on the
    // periodic tick (bounds tail latency for a trickle), whichever comes first.
    private const int FlushAfterWrites = 256;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly SessionId _sessionId;
    private readonly string _sessionLogsBasePath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private StreamWriter? _writer;
    private int _unflushedWrites;
    private bool _flushFailing;

    public ITimerScheduler Timers { get; set; } = null!;

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
        Receive<FlushTick>(_ => Flush());
        Receive<ReceiveTimeout>(_ => Context.Stop(Self));
    }

    // Periodic flush signal. INotInfluenceReceiveTimeout so the 1s cadence never resets the
    // session's idle-stop timer.
    private sealed class FlushTick : INotInfluenceReceiveTimeout
    {
        public static readonly FlushTick Instance = new();
        private FlushTick() { }
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

        _writer = new StreamWriter(stream) { AutoFlush = false };
        Timers.StartPeriodicTimer("session-log-flush", FlushTick.Instance, FlushInterval);
    }

    protected override void PostStop()
    {
        // Flush explicitly first so a write failure is reported via Flush's rate-limited warning.
        // Dispose flushes again; guard it so a still-failing disk (the _flushFailing state) cannot
        // throw IOException out of PostStop and bury the real cause under Akka's PostStop noise.
        Flush();
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to close session.log for {Session}", _sessionId.Value);
        }
        base.PostStop();
    }

    // Buffered write: lines accumulate in the StreamWriter buffer and are flushed in batches.
    // Used for the high-volume diagnostic lines, where an fsync per line would dominate.
    private void Write(string line)
    {
        _writer?.WriteLine(line);
        if (++_unflushedWrites >= FlushAfterWrites)
            Flush();
    }

    // Durable write for the audit transcript (user/assistant/tool/usage lines): flush immediately
    // so a hard process death (SIGKILL/OOM) cannot drop the security/audit record's tail. The
    // flush also drains any diagnostics buffered before this line, so audit lines are natural
    // flush points. Diagnostics themselves stay on the batched Write() path above.
    private void WriteDurable(string line)
    {
        Write(line);
        Flush();
    }

    private void Flush()
    {
        // Nothing buffered AND not in a failing state → skip. While failing, fall through so the
        // periodic 1s tick keeps retrying the flush (the bytes are still in the StreamWriter
        // buffer) and becomes durable as soon as the disk recovers — even with no new writes.
        if (_unflushedWrites == 0 && !_flushFailing)
            return;

        try
        {
            _writer?.Flush();
            _unflushedWrites = 0;
            if (_flushFailing)
            {
                _flushFailing = false;
                _log.Info("session.log flushing recovered for {Session}", _sessionId.Value);
            }
        }
        catch (Exception ex)
        {
            // Reset the write-batch counter so writes don't trigger a per-line flush storm during a
            // persistent failure (full disk, locked file); _flushFailing stays set, so the 1s tick
            // keeps retrying until recovery. Warn once on onset, once on recovery.
            _unflushedWrites = 0;
            if (!_flushFailing)
            {
                _flushFailing = true;
                _log.Warning(ex, "Failed to flush session.log for {Session}; further flush failures suppressed until recovery", _sessionId.Value);
            }
        }
    }

    private void OnUserMessage(SendUserMessage msg)
    {
        try
        {
            var mediaNote = msg.MediaReferences.Count > 0
                ? $" (+{msg.MediaReferences.Count} media)"
                : string.Empty;
            var line = $"[{_timeProvider.GetUtcNow():o}] User: {TextTruncation.EllipsisAppend(msg.Content, 1000)}{mediaNote}";

            WriteDurable(line);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Dropped user message audit line for {Session}", _sessionId.Value);
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
                ToolActivityOutput activity =>
                    $"Tool activity: {activity.ToolName} (call={activity.CallId}, turn={activity.TurnId}) " +
                    $"phase={activity.Phase} summary={activity.Summary ?? "-"}",
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
                    $"SubAgent started: {sa.AgentName} (run={sa.RunId?.Value ?? "-"}, parent={sa.ParentCallId?.Value ?? "-"}, tools={sa.ToolCount})",
                SubAgentOutput sa when sa.Phase == SubAgentPhase.Activity =>
                    $"SubAgent activity: {sa.AgentName} (run={sa.RunId?.Value ?? "-"}, parent={sa.ParentCallId?.Value ?? "-"}, phase={sa.ActivityPhase ?? "active"}) {sa.ActivitySummary ?? string.Empty}",
                SubAgentOutput sa =>
                    $"SubAgent completed: {sa.AgentName} (run={sa.RunId?.Value ?? "-"}, parent={sa.ParentCallId?.Value ?? "-"}, success={sa.Success}, outcome={sa.Outcome.ToString().ToLowerInvariant()}, reason={sa.OutcomeReason?.Value ?? "-"}, duration={sa.Duration.TotalSeconds:F1}s, findings={sa.FindingsCount}, memory={sa.MemoryDecision ?? "n/a"}{(string.IsNullOrWhiteSpace(sa.MemoryDecisionReason) ? string.Empty : $", memoryReason={sa.MemoryDecisionReason}")})",
                ErrorOutput error => $"Error [{error.Category}] (ref: {error.CorrelationId:N}): {error.Message}",
                FileOutput file => $"File: {file.FileName} ({file.MimeType})",
                _ => null
            };

            if (line is not null)
            {
                WriteDurable($"[{_timeProvider.GetUtcNow():o}] {line}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Dropped session log audit line for {Session}", _sessionId.Value);
        }
    }

    private void OnDiagnostic(SessionLogDiagnostic diagnostic)
    {
        try
        {
            Write(diagnostic.Line);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Dropped diagnostic audit line for {Session}", _sessionId.Value);
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
