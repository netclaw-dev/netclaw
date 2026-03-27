using System.Text;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Per-session persistent child actor that observes the conversation stream
/// and distills memories when the session goes idle.
///
/// <para>The observer accumulates a transcript from forwarded messages:
/// user input, assistant text, tool call names, recalled memories, loaded skills,
/// and turn boundaries. When the stream goes quiet (ReceiveTimeout), it runs a
/// sidecar LLM call to distill the transcript into memory proposals.</para>
///
/// <para>Proposals are sent to the parent (<see cref="LlmSessionActor"/>),
/// which routes them to the <see cref="MemoryCurationActor"/> for dedup and
/// persistence. Token usage from the sidecar call is included in the result
/// so the parent can emit it through the standard usage pipeline.</para>
///
/// <para>Persistent: journals <see cref="MemoriesDistilled"/> events so the
/// skip list of already-proposed anchors survives across session incarnations.</para>
/// </summary>
public sealed class SessionMemoryObserverActor : ReceivePersistentActor
{
    private readonly SessionId _sessionId;
    private readonly IChatClient _client;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _sidecarTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private readonly StringBuilder _transcript = new();
    private readonly HashSet<string> _proposedAnchors = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasNewContent;
    private bool _draining;
    private long _contentVersion;
    private long _nextRunId;
    private DistillationRunState? _activeRun;
    private PendingPassivationRequest? _pendingPassivation;
    private int _turnCount;

    public override string PersistenceId { get; }

    public static Props CreateProps(
        SessionId sessionId,
        IChatClient client,
        TimeSpan idleTimeout,
        TimeSpan sidecarTimeout,
        TimeProvider? timeProvider = null) =>
        Props.Create(() => new SessionMemoryObserverActor(sessionId, client, idleTimeout, sidecarTimeout, timeProvider));

    public SessionMemoryObserverActor(
        SessionId sessionId,
        IChatClient client,
        TimeSpan idleTimeout,
        TimeSpan sidecarTimeout,
        TimeProvider? timeProvider = null)
    {
        _sessionId = sessionId;
        _client = client;
        _idleTimeout = idleTimeout;
        _sidecarTimeout = sidecarTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;

        PersistenceId = $"memory-observer-{sessionId.Value}";

        Recover<MemoriesDistilled>(evt =>
        {
            foreach (var anchor in evt.Anchors)
                _proposedAnchors.Add(anchor);
        });

        Recover<RecoveryCompleted>(_ =>
        {
            _log.Info(
                "session_observer_recovery_complete proposedAnchors={Count}",
                _proposedAnchors.Count);
        });

        Command<SendUserMessage>(msg =>
        {
            if (!string.IsNullOrWhiteSpace(msg.Content))
                AppendTranscriptLine($"[user] {msg.Content}");
        });

        Command<ObserverSystemContext>(msg => AppendTranscriptLine($"[{msg.Label}] {msg.Content}"));

        Command<SessionOutput>(msg =>
        {
            var line = FormatOutput(msg);
            if (line is not null)
                AppendTranscriptLine(line);

            if (msg is TurnCompleted tc && tc.Outcome != TurnOutcome.Skipped)
                _turnCount = tc.TurnNumber;
        });

        Command<ReceiveTimeout>(_ => TriggerDistillation(Context.Parent, replyWhenNoWork: false));
        Command<DistillMemories>(_ => TriggerDistillation(Sender, replyWhenNoWork: true));

        Command<SessionPhaseChanged>(msg =>
        {
            if (msg.Phase == SessionPhase.Passivating)
            {
                _draining = true;
                Context.SetReceiveTimeout(null);
                _log.Info("session_observer_draining reason=passivating");
            }
            else if (_draining)
            {
                _draining = false;
                _pendingPassivation = null;
                Context.SetReceiveTimeout(_idleTimeout);
                _log.Info("session_observer_resumed reason=passivation_aborted phase={Phase}", msg.Phase);
            }
        });

        Command<DistillationRunCompleted>(HandleDistillationRunCompleted);

        Context.SetReceiveTimeout(_idleTimeout);
    }

    private void TriggerDistillation(IActorRef replyTo, bool replyWhenNoWork)
    {
        if (_activeRun is not null)
        {
            _log.Info("session_observer_distill_deferred reason=already_in_progress");
            if (replyWhenNoWork)
                _pendingPassivation = new PendingPassivationRequest(replyTo, _contentVersion);
            return;
        }

        if (!_hasNewContent)
        {
            _log.Info("session_observer_distill_skipped reason=no_new_content");
            if (replyWhenNoWork)
                replyTo.Tell(SessionDistillationCompleted.Empty);
            return;
        }

        var transcriptText = _transcript.ToString();
        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            _log.Info("session_observer_distill_skipped reason=empty_transcript");
            if (replyWhenNoWork)
                replyTo.Tell(SessionDistillationCompleted.Empty);
            return;
        }

        StartDistillation(replyTo, transcriptText);
    }

    private void StartDistillation(IActorRef replyTo, string transcriptText)
    {
        var run = new DistillationRunState(++_nextRunId, _contentVersion, replyTo);
        _activeRun = run;

        _ = RunDistillationAsync(
            _client,
            _sessionId.Value,
            _sessionId.ToMemoryDomain(),
            _turnCount,
            transcriptText,
            _proposedAnchors.ToArray(),
            _sidecarTimeout,
            Self,
            run.RunId,
            run.ContentVersion);
    }

    private void HandleDistillationRunCompleted(DistillationRunCompleted msg)
    {
        if (_activeRun is not { } activeRun || activeRun.RunId != msg.RunId)
        {
            _log.Info("session_observer_distill_ignored reason=stale_completion runId={RunId}", msg.RunId);
            return;
        }

        _activeRun = null;

        if (msg.FailureReason is null && msg.ContentVersion < _contentVersion)
        {
            _log.Info(
                "session_observer_distill_superseded runId={RunId} runVersion={RunVersion} currentVersion={CurrentVersion}",
                msg.RunId,
                msg.ContentVersion,
                _contentVersion);

            if (_pendingPassivation is { } pending)
                StartDistillation(pending.ReplyTo, _transcript.ToString());

            return;
        }

        PersistAnchorsThenDispatch(activeRun, msg);
    }

    private void PersistAnchorsThenDispatch(DistillationRunState activeRun, DistillationRunCompleted msg)
    {
        void Dispatch()
        {
            if (msg.FailureReason is null && activeRun.ContentVersion == _contentVersion)
                _hasNewContent = false;

            var completion = new SessionDistillationCompleted
            {
                Proposals = msg.Proposals,
                InputTokens = msg.InputTokens,
                OutputTokens = msg.OutputTokens,
                FailureReason = msg.FailureReason
            };

            IActorRef? additionalReplyTo = null;
            if (_pendingPassivation is { } pending)
            {
                if (msg.FailureReason is not null || activeRun.ContentVersion >= pending.RequiredContentVersion)
                {
                    if (!pending.ReplyTo.Equals(activeRun.ReplyTo))
                        additionalReplyTo = pending.ReplyTo;

                    _pendingPassivation = null;
                }
            }

            activeRun.ReplyTo.Tell(completion);

            if (additionalReplyTo is not null)
            {
                if (msg.FailureReason is null)
                    additionalReplyTo.Tell(SessionDistillationCompleted.Empty);
                else
                    additionalReplyTo.Tell(completion);
            }
        }

        if (msg.FailureReason is null && msg.Anchors.Count > 0)
        {
            var evt = new MemoriesDistilled(msg.Anchors, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            Persist(evt, e =>
            {
                foreach (var anchor in e.Anchors)
                    _proposedAnchors.Add(anchor);

                _log.Info("session_observer_anchors_persisted count={Count}", e.Anchors.Count);
                Dispatch();
            });
            return;
        }

        Dispatch();
    }

    private void AppendTranscriptLine(string line)
    {
        _transcript.AppendLine(line);
        _hasNewContent = true;
        _contentVersion++;
    }

    private async Task RunDistillationAsync(
        IChatClient client,
        string sessionId,
        string domain,
        int turnCount,
        string transcript,
        IReadOnlyList<string> skipAnchors,
        TimeSpan timeout,
        IActorRef self,
        long runId,
        long contentVersion)
    {
        long? inputTokens = null;
        long? outputTokens = null;

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var messages = new List<ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, BuildDistillationSystemPrompt()),
                new(Microsoft.Extensions.AI.ChatRole.User, BuildDistillationUserPrompt(
                    sessionId, domain, turnCount, transcript, skipAnchors))
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var text = response.Messages[^1].Text ?? string.Empty;

            inputTokens = response.Usage?.InputTokenCount;
            outputTokens = response.Usage?.OutputTokenCount;

            var proposals = ParseProposals(text);
            var newAnchors = proposals
                .Where(p => p.Anchor is not null)
                .Select(p => p.Anchor!.CanonicalName)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            self.Tell(new DistillationRunCompleted(
                runId,
                contentVersion,
                proposals,
                newAnchors,
                inputTokens,
                outputTokens,
                null));
        }
        catch (Exception ex)
        {
            self.Tell(new DistillationRunCompleted(
                runId,
                contentVersion,
                [],
                [],
                inputTokens,
                outputTokens,
                ex.Message));
        }
    }

    private static IReadOnlyList<MemoryProposal> ParseProposals(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var direct = TryDeserialize(text);
        if (direct is not null)
            return direct;

        var jsonStart = text.IndexOf('{', StringComparison.Ordinal);
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var extracted = TryDeserialize(text[jsonStart..(jsonEnd + 1)]);
            if (extracted is not null)
                return extracted;
        }

        return [];
    }

    private static IReadOnlyList<MemoryProposal>? TryDeserialize(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<DistillationResponse>(json, JsonOptions);
            return result?.Proposals;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FormatOutput(SessionOutput output) => output switch
    {
        TextOutput text when !string.IsNullOrWhiteSpace(text.Text)
            => $"[assistant] {text.Text}",
        ToolCallOutput toolCall
            => $"[tool] {toolCall.ToolName}({Truncate(toolCall.ArgumentsJson ?? "", 200)})",
        TurnCompleted tc
            => $"[turn {tc.TurnNumber} {tc.Outcome.ToString().ToLowerInvariant()}]",
        CompactionOutput
            => "[session compacted]",
        SubAgentOutput sa when sa.Phase == SubAgentPhase.Completed
            => $"[subagent] {sa.AgentName} completed (findings={sa.FindingsCount})",
        ErrorOutput error
            => $"[error] {error.Message}",
        _ => null
    };

    internal static string BuildDistillationSystemPrompt() => $$"""
        You are a session memory distillation sidecar.
        You receive the full transcript of a conversation session.
        Return JSON only: { "proposals": [ ... ] }

        Your job: identify the 2-5 most valuable things learned in this session.

        What to extract:
        - Stable user preferences, decisions, or assertions -> durable_fact (recallMode: "auto")
        - Agent conclusions from research, analysis, or tool use -> evidence (recallMode: "searchable")
        - Project facts, constraints, or architectural decisions -> durable_fact (recallMode: "auto")
        - Task outcomes and significant results -> evidence (recallMode: "searchable")

        {{MemorySidecarPromptBuilder.BuildClassificationRules()}}

        What to skip:
        - Greetings, pleasantries, task coordination ("can you do X" / "sure")
        - Intermediate reasoning superseded by a later conclusion
        - Information already present in [recalled-memory] entries - those are already stored
        - Information from [loaded-skill] entries - those are system knowledge, not memories
        - Raw data or tool output that was summarized later (keep the summary, skip the raw)
        - Anything the user corrected or walked back
        - Anchors listed in the "skipAnchors" field - those were already proposed

        Focus on the narrative arc: What did this session accomplish?
        What would be useful to know in a future session?

        For each proposal, include:
        - operation: "upsert_document" for durable_fact, "append_record" for evidence (see rules above)
        - memoryClass: "durable_fact" or "evidence"
        - subjectKind: "user" or "project"
        - subjectValue: the subject identifier
        - anchor: { "canonicalName": "slug-name", "anchorType": "type" }
        - title, content, aliases (non-empty array), facets (non-empty array)
        - recallMode: "auto" for durable_fact, "searchable" for evidence
        - sensitivity: "normal"
        - confidence: 0.7-0.95

        Example durable_fact (stable knowledge — merged on write):
        {
          "operation": "upsert_document",
          "memoryClass": "durable_fact",
          "subjectKind": "user",
          "subjectValue": "self",
          "anchor": { "canonicalName": "user-preferred-editor", "anchorType": "preference" },
          "title": "Preferred Code Editor",
          "content": "User prefers VS Code with Vim keybindings.",
          "aliases": ["VS Code", "editor preference", "vim keybindings"],
          "facets": ["development_tools", "user_preference"],
          "recallMode": "auto",
          "sensitivity": "normal",
          "confidence": 0.92
        }

        Example evidence (point-in-time finding — immutable, never merged):
        {
          "operation": "append_record",
          "memoryClass": "evidence",
          "subjectKind": "project",
          "subjectValue": "netclaw",
          "anchor": { "canonicalName": "memory-curation-perf-analysis", "anchorType": "analysis" },
          "title": "Memory Curation Performance Analysis",
          "content": "Curation dedup runs in <2ms per proposal with SQLite FTS5. Bottleneck is the LLM tier at ~800ms per ambiguous case.",
          "aliases": ["curation performance", "dedup latency"],
          "facets": ["performance_analysis", "project_artifact"],
          "recallMode": "searchable",
          "sensitivity": "normal",
          "confidence": 0.78
        }

        If nothing is worth remembering, return { "proposals": [] }.
        """;

    internal static string BuildDistillationUserPrompt(
        string sessionId,
        string domain,
        int turnCount,
        string transcript,
        IReadOnlyList<string> skipAnchors) => JsonSerializer.Serialize(new
    {
        sessionId,
        domain,
        turnCount,
        skipAnchors,
        transcript
    });

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record PendingPassivationRequest(IActorRef ReplyTo, long RequiredContentVersion);

    private sealed record DistillationRunState(long RunId, long ContentVersion, IActorRef ReplyTo);

    private sealed record DistillationRunCompleted(
        long RunId,
        long ContentVersion,
        IReadOnlyList<MemoryProposal> Proposals,
        IReadOnlyList<string> Anchors,
        long? InputTokens,
        long? OutputTokens,
        string? FailureReason) : INotInfluenceReceiveTimeout;
}

/// <summary>Journaled event recording which anchors were proposed in a distillation.</summary>
public sealed record MemoriesDistilled(IReadOnlyList<string> Anchors, long TimestampMs);

/// <summary>System context injection forwarded to the observer (recalled memories, loaded skills).</summary>
public sealed record ObserverSystemContext(string Label, string Content);

/// <summary>Signal from parent: distill now (used during passivation).</summary>
public sealed record DistillMemories;

/// <summary>Observer's response with memory proposals and token usage for billing.</summary>
public sealed record SessionDistillationCompleted : INotInfluenceReceiveTimeout
{
    public static readonly SessionDistillationCompleted Empty = new() { Proposals = [] };

    public required IReadOnlyList<MemoryProposal> Proposals { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public string? FailureReason { get; init; }
}

internal sealed record DistillationResponse(IReadOnlyList<MemoryProposal>? Proposals);
