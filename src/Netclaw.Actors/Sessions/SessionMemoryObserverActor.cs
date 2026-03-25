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
    private readonly TimeSpan _sidecarTimeout;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private readonly StringBuilder _transcript = new();
    private readonly HashSet<string> _proposedAnchors = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasNewContent;
    private bool _distilling;
    private int _turnCount;

    public override string PersistenceId { get; }

    public static Props CreateProps(
        SessionId sessionId,
        IChatClient client,
        TimeSpan idleTimeout,
        TimeSpan sidecarTimeout) =>
        Props.Create(() => new SessionMemoryObserverActor(sessionId, client, idleTimeout, sidecarTimeout));

    public SessionMemoryObserverActor(
        SessionId sessionId,
        IChatClient client,
        TimeSpan idleTimeout,
        TimeSpan sidecarTimeout)
    {
        _sessionId = sessionId;
        _client = client;
        _sidecarTimeout = sidecarTimeout;

        PersistenceId = $"memory-observer-{sessionId.Value}";

        // Recovery: rebuild skip list from journaled events
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

        // Stream messages from parent
        Command<SendUserMessage>(msg =>
        {
            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                _transcript.AppendLine($"[user] {msg.Content}");
                _hasNewContent = true;
            }
        });

        Command<ObserverSystemContext>(msg =>
        {
            _transcript.AppendLine($"[{msg.Label}] {msg.Content}");
            _hasNewContent = true;
        });

        Command<SessionOutput>(msg =>
        {
            var line = FormatOutput(msg);
            if (line is not null)
            {
                _transcript.AppendLine(line);
                _hasNewContent = true;
            }

            if (msg is TurnCompleted tc)
                _turnCount = tc.TurnNumber;
        });

        // Idle timer: self-trigger distillation when stream goes quiet
        Command<ReceiveTimeout>(_ => TriggerDistillation(Context.Parent, replyWhenNoWork: false));

        // Explicit distillation request from parent (passivation)
        Command<DistillMemories>(_ => TriggerDistillation(Sender, replyWhenNoWork: true));

        // Internal: mark distillation complete. On failure, re-enable hasNewContent for retry.
        Command<DistillationFinished>(msg =>
        {
            _distilling = false;
            if (msg.Success)
                _hasNewContent = false;
        });

        // Internal: persist proposed anchors to journal (arrives from async task via self.Tell)
        Command<PersistAnchors>(msg =>
        {
            var evt = new MemoriesDistilled(msg.Anchors, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Persist(evt, e =>
            {
                foreach (var anchor in e.Anchors)
                    _proposedAnchors.Add(anchor);
                _log.Info("session_observer_anchors_persisted count={Count}", e.Anchors.Count);
            });
        });

        // Set the idle timer
        Context.SetReceiveTimeout(idleTimeout);
    }

    private void TriggerDistillation(IActorRef replyTo, bool replyWhenNoWork)
    {
        if (_distilling)
        {
            _log.Info("session_observer_distill_skipped reason=already_in_progress");
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

        _distilling = true;

        var self = Self;
        var skipList = _proposedAnchors.ToArray();

        _ = RunDistillationAsync(
            _client, _sessionId.Value, _sessionId.ToMemoryDomain(),
            _turnCount, transcriptText, skipList, _sidecarTimeout, self, replyTo);
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
        IActorRef replyTo)
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

            // Persist proposed anchors to journal for skip list durability
            var newAnchors = proposals
                .Where(p => p.Anchor is not null)
                .Select(p => p.Anchor!.CanonicalName)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (newAnchors.Length > 0)
            {
                self.Tell(new PersistAnchors(newAnchors));
            }

            replyTo.Tell(new SessionDistillationCompleted
            {
                Proposals = proposals,
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            });
        }
        catch (Exception ex)
        {
            replyTo.Tell(new SessionDistillationCompleted
            {
                Proposals = [],
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                FailureReason = ex.Message
            });
            self.Tell(new DistillationFinished(false));
            return;
        }

        self.Tell(new DistillationFinished(true));
    }

    private static IReadOnlyList<MemoryProposal> ParseProposals(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Try direct parse first, then extract from markdown fences
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
            => $"[turn {tc.TurnNumber} completed]",
        CompactionOutput
            => "[session compacted]",
        SubAgentOutput sa when sa.Phase == SubAgentPhase.Completed
            => $"[subagent] {sa.AgentName} completed (findings={sa.FindingsCount})",
        ErrorOutput error
            => $"[error] {error.Message}",
        _ => null
    };

    internal static string BuildDistillationSystemPrompt() => """
        You are a session memory distillation sidecar.
        You receive the full transcript of a conversation session.
        Return JSON only: { "proposals": [ ... ] }

        Your job: identify the 2-5 most valuable things learned in this session.

        What to extract:
        - Stable user preferences, decisions, or assertions → durable_fact (recallMode: "auto")
        - Agent conclusions from research, analysis, or tool use → evidence (recallMode: "searchable")
        - Project facts, constraints, or architectural decisions → durable_fact (recallMode: "auto")
        - Task outcomes and significant results → evidence (recallMode: "searchable")

        What to skip:
        - Greetings, pleasantries, task coordination ("can you do X" / "sure")
        - Intermediate reasoning superseded by a later conclusion
        - Information already present in [recalled-memory] entries — those are already stored
        - Information from [loaded-skill] entries — those are system knowledge, not memories
        - Raw data or tool output that was summarized later (keep the summary, skip the raw)
        - Anything the user corrected or walked back
        - Anchors listed in the "skipAnchors" field — those were already proposed

        Focus on the narrative arc: What did this session accomplish?
        What would be useful to know in a future session?

        For each proposal, include:
        - operation: "upsert_document" or "append_record"
        - memoryClass: "durable_fact" or "evidence"
        - subjectKind: "user" or "project"
        - subjectValue: the subject identifier
        - anchor: { "canonicalName": "slug-name", "anchorType": "type" }
        - title, content, aliases (non-empty array), facets (non-empty array)
        - recallMode: "auto" for durable_fact, "searchable" for evidence
        - sensitivity: "normal"
        - confidence: 0.7-0.95

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

    // ── Internal messages ──

    private sealed record DistillationFinished(bool Success);

    private sealed record PersistAnchors(IReadOnlyList<string> Anchors);
}

// ── Journal event ──

/// <summary>Journaled event recording which anchors were proposed in a distillation.</summary>
public sealed record MemoriesDistilled(IReadOnlyList<string> Anchors, long TimestampMs);

// ── Messages ──

/// <summary>System context injection forwarded to the observer (recalled memories, loaded skills).</summary>
public sealed record ObserverSystemContext(string Label, string Content);

/// <summary>Signal from parent: distill now (used during passivation).</summary>
public sealed record DistillMemories;

/// <summary>Observer's response with memory proposals and token usage for billing.</summary>
public sealed record SessionDistillationCompleted
{
    public static readonly SessionDistillationCompleted Empty = new() { Proposals = [] };

    public required IReadOnlyList<MemoryProposal> Proposals { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public string? FailureReason { get; init; }
}

internal sealed record DistillationResponse(IReadOnlyList<MemoryProposal>? Proposals);
