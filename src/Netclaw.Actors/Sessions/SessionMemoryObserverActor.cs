// -----------------------------------------------------------------------
// <copyright file="SessionMemoryObserverActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;

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
/// <para>Persistent: journals <see cref="MemoriesDistilledV2"/> events so the
/// skip list of already-proposed anchors survives across session incarnations.</para>
/// </summary>
public sealed class SessionMemoryObserverActor : ReceivePersistentActor
{
    private readonly SessionId _sessionId;
    private readonly IChatClient _client;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _sidecarTimeout;
    private readonly int _distillTurnInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private readonly StringBuilder _transcript = new();
    private readonly HashSet<string> _proposedAnchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProposedMemoryContext> _proposedMemories = [];
    private bool _hasNewContent;
    private bool _draining;
    private long _contentVersion;
    private long _nextRunId;
    private DistillationRunState? _activeRun;
    private PendingPassivationRequest? _pendingPassivation;
    private int _turnCount;
    private int _turnsSinceLastDistill;

    public override string PersistenceId { get; }

    public static Props CreateProps(
        SessionId sessionId,
        IChatClient client,
        TimeSpan idleTimeout,
        TimeSpan sidecarTimeout,
        int distillTurnInterval = 0,
        TimeProvider? timeProvider = null) =>
        Props.Create(() => new SessionMemoryObserverActor(sessionId, client, idleTimeout, sidecarTimeout, distillTurnInterval, timeProvider));

    public SessionMemoryObserverActor(
        SessionId sessionId,
        IChatClient client,
        TimeSpan idleTimeout,
        TimeSpan sidecarTimeout,
        int distillTurnInterval = 0,
        TimeProvider? timeProvider = null)
    {
        _sessionId = sessionId;
        _client = client;
        _idleTimeout = idleTimeout;
        _sidecarTimeout = sidecarTimeout;
        _distillTurnInterval = distillTurnInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;

        PersistenceId = $"memory-observer-{sessionId.Value}";

        Recover<MemoriesDistilledV2>(evt =>
        {
            foreach (var anchor in evt.Anchors)
                _proposedAnchors.Add(anchor);
            _proposedMemories.AddRange(evt.Proposals);
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
            {
                _turnCount = tc.TurnNumber.Value;
                _turnsSinceLastDistill++;

                if (_distillTurnInterval > 0 && _hasNewContent && !_draining
                    && _turnsSinceLastDistill >= _distillTurnInterval)
                {
                    _log.Info(
                        "session_observer_turn_trigger turns_since_last={TurnsSince} interval={Interval}",
                        _turnsSinceLastDistill, _distillTurnInterval);
                    TriggerDistillation(Context.Parent, replyWhenNoWork: false);
                }
            }
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
        Command<RecordAcceptedDistillationProposals>(HandleRecordAcceptedDistillationProposals);

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
        _turnsSinceLastDistill = 0;

        _ = RunDistillationAsync(
            _client,
            _sessionId,
            _turnCount,
            transcriptText,
            _proposedMemories.ToArray(),
            _sidecarTimeout,
            Self,
            run.RunId,
            run.ContentVersion,
            _log);
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

        Dispatch();
    }

    private void HandleRecordAcceptedDistillationProposals(RecordAcceptedDistillationProposals msg)
    {
        // Capture Sender into a local before any async boundary: inside the
        // Persist callback below, Akka's Sender property reflects whichever
        // message is currently being processed, not the one that triggered
        // this handler. If any message arrives at this actor between the
        // Persist call and the callback firing, Sender would be silently
        // overwritten and the reply would go to the wrong target (or to
        // DeadLetters).
        var replyTo = Sender;

        if (msg.Proposals.Count == 0)
        {
            replyTo.Tell(AcceptedDistillationProposalsRecorded.Instance);
            return;
        }

        var proposalContexts = msg.Proposals
            .Where(p => p.Anchor is not null && !string.IsNullOrWhiteSpace(p.Anchor.CanonicalName))
            .Select(p => new ProposedMemoryContext(p.Anchor!.CanonicalName, p.Title, p.Content))
            .DistinctBy(p => p.Anchor, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (proposalContexts.Length == 0)
        {
            replyTo.Tell(AcceptedDistillationProposalsRecorded.Instance);
            return;
        }

        var anchors = proposalContexts
            .Select(p => p.Anchor)
            .ToArray();

        var evt = new MemoriesDistilledV2(anchors, proposalContexts, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        Persist(evt, e =>
        {
            foreach (var anchor in e.Anchors)
                _proposedAnchors.Add(anchor);

            foreach (var proposal in e.Proposals)
            {
                _proposedMemories.RemoveAll(existing => string.Equals(existing.Anchor, proposal.Anchor, StringComparison.OrdinalIgnoreCase));
                _proposedMemories.Add(proposal);
            }

            _log.Info("session_observer_accepted_proposals_persisted count={Count}", e.Proposals.Count);
            replyTo.Tell(AcceptedDistillationProposalsRecorded.Instance);
        });
    }

    private void AppendTranscriptLine(string line)
    {
        _transcript.AppendLine(line);
        _hasNewContent = true;
        _contentVersion++;
    }

    internal static async Task RunDistillationAsync(
        IChatClient client,
        SessionId sessionId,
        int turnCount,
        string transcript,
        IReadOnlyList<ProposedMemoryContext> existingProposals,
        TimeSpan timeout,
        IActorRef self,
        long runId,
        long contentVersion,
        ILoggingAdapter log)
    {
        long? inputTokens = null;
        long? outputTokens = null;

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var diagnosticsScope = SessionDiagnosticsContext.Push(sessionId.Value);
            var messages = new List<ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, DistillationSystemPrompt),
                new(Microsoft.Extensions.AI.ChatRole.User, BuildDistillationUserPrompt(
                    sessionId, turnCount, transcript, existingProposals))
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var text = response.Messages[^1].Text ?? string.Empty;

            inputTokens = response.Usage?.InputTokenCount;
            outputTokens = response.Usage?.OutputTokenCount;

            var parseResult = ParseProposals(text);
            switch (parseResult.Outcome)
            {
                case ParseProposalsOutcome.NoJsonFound:
                    log.Info(
                        "session_observer_parse_no_json rawLength={RawLength} candidateCount={CandidateCount} preview={Preview}",
                        text.Length,
                        parseResult.CandidateCount,
                        parseResult.Preview);
                    break;
                case ParseProposalsOutcome.ParseFailed:
                    log.Info(
                        "session_observer_parse_failed rawLength={RawLength} candidateCount={CandidateCount} preview={Preview}",
                        text.Length,
                        parseResult.CandidateCount,
                        parseResult.Preview);
                    break;
            }
            var proposals = parseResult.Proposals;
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

    /// <summary>
    /// Outcome of a <see cref="ParseProposals"/> call. Returned alongside the
    /// proposal list so callers can emit structured log events (with named
    /// placeholders) for the failure modes without the parser needing an
    /// Akka <c>ILoggingAdapter</c> dependency.
    /// </summary>
    internal enum ParseProposalsOutcome
    {
        /// <summary>Input was null or whitespace; no LLM call was wasted.</summary>
        EmptyInput,
        /// <summary>Proposals were successfully extracted from the response.</summary>
        Success,
        /// <summary>
        /// No JSON object could be located in the response at all
        /// (e.g. refusal text, malformed output with no braces).
        /// </summary>
        NoJsonFound,
        /// <summary>
        /// One or more JSON object candidates were located but none deserialized
        /// into a <c>DistillationResponse</c> with a non-null <c>Proposals</c>
        /// array. Distinct from <see cref="NoJsonFound"/> because it points at
        /// schema/format issues rather than the model not producing JSON at all.
        /// </summary>
        ParseFailed,
    }

    internal sealed record ParseProposalsResult(
        IReadOnlyList<MemoryProposal> Proposals,
        ParseProposalsOutcome Outcome,
        int CandidateCount,
        string? Preview);

    internal static ParseProposalsResult ParseProposals(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ParseProposalsResult([], ParseProposalsOutcome.EmptyInput, 0, null);

        var stripped = StripMarkdownFences(text);

        var direct = TryDeserialize(stripped);
        if (direct is not null)
            return new ParseProposalsResult(direct, ParseProposalsOutcome.Success, 0, null);

        var candidates = ExtractJsonObjectCandidates(stripped);
        foreach (var candidate in candidates)
        {
            var parsed = TryDeserialize(candidate);
            if (parsed is not null)
                return new ParseProposalsResult(parsed, ParseProposalsOutcome.Success, candidates.Count, null);
        }

        var preview = TextTruncation.EllipsisAppend(text.Trim(), 200);
        var outcome = candidates.Count == 0
            ? ParseProposalsOutcome.NoJsonFound
            : ParseProposalsOutcome.ParseFailed;
        return new ParseProposalsResult([], outcome, candidates.Count, preview);
    }

    internal static string StripMarkdownFences(string text)
    {
        // Fast path: skip the trim allocation when the input clearly has no
        // fence. Any leading whitespace is preserved unchanged because the
        // caller (ParseProposals) re-checks via direct deserialization first.
        if (text.Length == 0 || !ContainsFenceMarker(text))
            return text;

        var trimmed = text.Trim();
        if (trimmed.Length < 7 || !trimmed.StartsWith("```", StringComparison.Ordinal))
            return text;

        var openEnd = trimmed.IndexOf('\n', StringComparison.Ordinal);
        if (openEnd < 0)
            return text;

        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence <= openEnd)
            return text;

        return trimmed[(openEnd + 1)..closingFence].Trim();
    }

    private static bool ContainsFenceMarker(string text)
    {
        // Scan once for any `` ` `` — far cheaper than a string-allocating
        // .Trim() before we know whether a fence is even present.
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`')
                return true;
        }
        return false;
    }

    internal static IReadOnlyList<string> ExtractJsonObjectCandidates(string text)
    {
        var candidates = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{')
            {
                i++;
                continue;
            }

            var end = FindMatchingClose(text, i);
            if (end < 0)
                break;

            candidates.Add(text[i..(end + 1)]);
            i = end + 1;
        }
        return candidates;
    }

    private static int FindMatchingClose(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (inString)
            {
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '"')
                    inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return i;
                    break;
            }
        }
        return -1;
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
            => $"[tool] {toolCall.ToolName}({TextTruncation.EllipsisAppend(toolCall.ArgumentsJson ?? "", 200)})",
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

    private static readonly string DistillationSystemPrompt = BuildDistillationSystemPrompt();

    internal static string BuildDistillationSystemPrompt() => $$"""
        You are a session memory distillation sidecar.
        You receive a conversation transcript and a list of memories already
        proposed in this session (existingProposals).

        ## Output contract (strict)

        Return ONLY a single JSON object matching the schema below.
        Do NOT include any text before or after the JSON object.
        Do NOT wrap the JSON in markdown code fences (no ```json, no ```).
        Do NOT include explanations, reasoning, preamble, or commentary outside the JSON.
        Your entire response must start with `{` and end with `}`.

        Top-level shape: { "proposals": [ ... ] }

        Your job: curate the user's memory store based on this session.

        ## Workflow

        1. Identify what was learned in this session worth retaining
        2. Check existingProposals — if new information enriches an existing
           memory, re-use that anchor canonicalName. The system will merge
           your content into the existing entry.
        3. Only create a new anchor for genuinely new topics not covered by
           any existing proposal
        4. Skip anything trivial, sensitive, or already fully captured

        Yield 1-3 proposals. Fewer, richer entries beat many thin ones.
        Consolidate related facts into a single proposal.

        ## What to store (operational facts the agent can act on)

        - Tool, service, and account preferences (airlines, CRMs, editors)
        - Business and project context (company, products, team structure)
        - Travel logistics (home airport, loyalty programs, preferences)
        - Communication preferences and routines
        - Technical decisions, constraints, architectural choices
        - Workflow patterns and automation requirements

        ## What to never store

        - Credentials, API keys, tokens, passwords
        - Financial account numbers or balances
        - Medical or health information

        ## What to skip unless the user explicitly says "remember this"

        - Family or relationship dynamics
        - Personal opinions about specific people
        - Emotional processing or venting
        - Content shared as context that isn't itself a fact worth retaining

        ## What to skip (always)

        - Greetings, pleasantries, task coordination ("can you do X" / "sure")
        - Intermediate reasoning superseded by a later conclusion
        - Information already in [recalled-memory] entries — already stored
        - Information from [loaded-skill] entries — system knowledge, not memories
        - Raw data or tool output summarized later (keep summary, skip raw)
        - Anything the user corrected or walked back

        {{MemorySidecarPromptBuilder.BuildClassificationRules()}}

        ## Title rules

        - Clean, concise noun phrases (3-8 words)
        - Never use raw user quotes or speech artifacts as titles
        - Never prefix with the memory class ("Project Fact: ...")
        - Good: "Petabridge Marketing Automation Stack"
        - Bad: "Project Fact: Biggest need I has Right now is I need to monitor a"

        ## Deduplication

        For each entry in existingProposals:
        - If you have NEW information to add → re-use the same anchor name
        - If the transcript just restates what was captured → skip entirely
        - Only create a new anchor if the topic is clearly distinct

        ## Proposal format

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
        SessionId sessionId,
        int turnCount,
        string transcript,
        IReadOnlyList<ProposedMemoryContext> existingProposals) => JsonSerializer.Serialize(new
    {
        sessionId = sessionId.Value,
        turnCount,
        existingProposals = existingProposals.Select(p => new
        {
            anchor = p.Anchor,
            title = p.Title,
            content = p.Content
        }),
        transcript
    });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record PendingPassivationRequest(IActorRef ReplyTo, long RequiredContentVersion) : INoSerializationVerificationNeeded;

    private sealed record DistillationRunState(long RunId, long ContentVersion, IActorRef ReplyTo) : INoSerializationVerificationNeeded;

    private sealed record DistillationRunCompleted(
        long RunId,
        long ContentVersion,
        IReadOnlyList<MemoryProposal> Proposals,
        IReadOnlyList<string> Anchors,
        long? InputTokens,
        long? OutputTokens,
        string? FailureReason) : INotInfluenceReceiveTimeout, INoSerializationVerificationNeeded;
}

/// <summary>Journaled event recording proposed anchors with title and content context for deduplication.</summary>
public sealed record MemoriesDistilledV2(
    IReadOnlyList<string> Anchors,
    IReadOnlyList<ProposedMemoryContext> Proposals,
    long TimestampMs) : INetclawSerializableMessage;

/// <summary>Context for a previously proposed memory, used for semantic dedup in subsequent distillation runs.</summary>
public sealed record ProposedMemoryContext(string Anchor, string Title, string Content);

/// <summary>Sent by the session actor after proposals survive gating and are accepted for curation.</summary>
public sealed record RecordAcceptedDistillationProposals(IReadOnlyList<MemoryProposal> Proposals) : INoSerializationVerificationNeeded;

/// <summary>Ack from the observer once accepted proposal dedup state has been persisted.</summary>
public sealed class AcceptedDistillationProposalsRecorded : INoSerializationVerificationNeeded
{
    public static readonly AcceptedDistillationProposalsRecorded Instance = new();

    private AcceptedDistillationProposalsRecorded()
    {
    }
}

/// <summary>System context injection forwarded to the observer (recalled memories, loaded skills).</summary>
public sealed record ObserverSystemContext(string Label, string Content) : INoSerializationVerificationNeeded;

/// <summary>Signal from parent: distill now (used during passivation).</summary>
public sealed record DistillMemories : INoSerializationVerificationNeeded;

/// <summary>Observer's response with memory proposals and token usage for billing.</summary>
public sealed record SessionDistillationCompleted : INotInfluenceReceiveTimeout, INoSerializationVerificationNeeded
{
    public static readonly SessionDistillationCompleted Empty = new() { Proposals = [] };

    public required IReadOnlyList<MemoryProposal> Proposals { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public string? FailureReason { get; init; }
}

internal sealed record DistillationResponse(IReadOnlyList<MemoryProposal>? Proposals) : INoSerializationVerificationNeeded;
