// -----------------------------------------------------------------------
// <copyright file="MemoryCurationPipeline.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Memory;

public enum MemoryExtractionDropReason
{
    None = 0,
    EmptyContent,
    EphemeralContent,
    SecretLikeContent,
    PolicyRejected,
    TurnCompleteRetired,
    FingerprintDuplicate,
    PayloadDeserializationFailed,
    ObservedProposalsEmpty,
}

public sealed record MemoryExtractionResult(
    IReadOnlyList<MemoryCheckpointCandidate> Candidates,
    MemoryExtractionDropReason DropReason,
    string? DropDetail = null);

public sealed record MemoryCheckpointPayload(
    string SessionId,
    string TriggerType,
    string Source,
    string Content,
    string? UserContent,
    string? AssistantContent,
    bool IsExplicitRequest,
    bool HasVerifiedToolFinding,
    bool IsCompactionBoundary,
    bool HasAcceptedSubAgentFinding,
    string Sensitivity,
    string RecallMode,
    double Confidence,
    string? MemoryId = null,
    string? UpdateOldText = null,
    string? UpdateNewText = null,
    bool Delete = false,
    string? Kind = null,
    string? Title = null,
    string? UpdateSemantics = null,
    IReadOnlyList<string>? Evidence = null,
    string? SupersedesRecordId = null,
    long? FreshnessAtMs = null,
    string? Boundary = null,
    string? Audience = null);

public sealed record MemoryCheckpointCandidate(
    MemoryKind Kind,
    MemoryClass MemoryClass,
    string AnchorCanonicalName,
    string AnchorType,
    string Title,
    string Content,
    MemoryUpdateSemantics UpdateSemantics,
    string Boundary,
    TrustAudience Audience,
    string Sensitivity,
    MemoryRecallMode RecallMode,
    double Confidence,
    string? AliasesJson,
    string? FacetsJson,
    string? SlotsJson,
    long? FreshnessAtMs,
    long? ExpiresAtMs,
    string? MemoryId,
    string? SupersedesRecordId = null);

public sealed class MemoryRulesFirstExtractor(MemoryPolicyEvaluator policy)
{
    public IReadOnlyList<MemoryCheckpointCandidate> Extract(
        MemoryCheckpointPayload payload,
        IReadOnlySet<string> fingerprintSet)
        => ExtractWithDiagnostics(payload, fingerprintSet).Candidates;

    public MemoryExtractionResult ExtractWithDiagnostics(
        MemoryCheckpointPayload payload,
        IReadOnlySet<string> fingerprintSet)
    {
        var results = new List<MemoryCheckpointCandidate>();

        var content = payload.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            return new MemoryExtractionResult(results, MemoryExtractionDropReason.EmptyContent);

        if (IsEphemeral(content))
            return new MemoryExtractionResult(results, MemoryExtractionDropReason.EphemeralContent);

        if (SecretOutputRedactor.ContainsSecretLikeContent(content))
            return new MemoryExtractionResult(results, MemoryExtractionDropReason.SecretLikeContent);

        var decision = policy.EvaluateWrite(
            payload.Sensitivity,
            payload.RecallMode,
            payload.Confidence,
            payload.IsExplicitRequest,
            payload.Audience);
        if (!decision.Allowed)
            return new MemoryExtractionResult(results, MemoryExtractionDropReason.PolicyRejected, decision.Reason);

        // The turn-complete lane is retired. Netclaw no longer enqueues a checkpoint on
        // each completed turn, but an installation that upgrades can still hold
        // turn-complete rows in the SQLite checkpoint queue. Drain such a row to zero
        // candidates. A drain keeps the old behavior, because the lane dropped almost
        // every turn. It also stops a queued turn transcript from becoming a memory
        // through the general path below.
        if (MemoryDomainEnumExtensions.TryFromWireValue(payload.TriggerType, out CheckpointTriggerType trigger)
            && trigger == CheckpointTriggerType.TurnComplete
            && !payload.IsExplicitRequest)
        {
            return new MemoryExtractionResult(results, MemoryExtractionDropReason.TurnCompleteRetired);
        }

        var memoryClass = ResolveMemoryClass(payload);

        var resolvedAudienceWire = MemoryPolicyEvaluator.ResolveAudience(payload.Audience, TrustAudience.Public);
        SecurityPolicyDefaults.TryParseAudience(resolvedAudienceWire, out var parsedAudience);
        var resolvedBoundary = MemoryPolicyScopeResolver.ResolveBoundary(payload.Boundary);

        var kind = ResolveKind(payload);
        var title = ResolveTitle(payload, kind, content);
        var updateSemantics = ResolveUpdateSemantics(payload, kind, memoryClass);
        var anchor = ResolveAnchor(content, (SessionId)payload.SessionId);
        var anchorType = kind == MemoryKind.Record ? "event" : "concept";
        var fingerprint = BuildFingerprint(kind.ToWireValue(), title, content);
        if (fingerprintSet.Contains(fingerprint))
            return new MemoryExtractionResult(results, MemoryExtractionDropReason.FingerprintDuplicate);

        results.Add(new MemoryCheckpointCandidate(
            Kind: kind,
            MemoryClass: memoryClass,
            AnchorCanonicalName: anchor,
            AnchorType: anchorType,
            Title: title,
            Content: content,
            UpdateSemantics: updateSemantics,
            Boundary: resolvedBoundary,
            Audience: parsedAudience,
            Sensitivity: payload.Sensitivity,
            RecallMode: ResolveRecallMode(payload, memoryClass),
            Confidence: payload.Confidence,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            FreshnessAtMs: payload.FreshnessAtMs,
            ExpiresAtMs: ResolveExpiry(payload, memoryClass),
            MemoryId: payload.MemoryId,
            SupersedesRecordId: payload.SupersedesRecordId));

        return new MemoryExtractionResult(results, MemoryExtractionDropReason.None);
    }

    private static MemoryClass ResolveMemoryClass(MemoryCheckpointPayload payload)
    {
        if (payload.IsExplicitRequest ||
            string.Equals(payload.TriggerType, CheckpointTriggerType.ExplicitMemoryRequest.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            return MemoryClass.DurableFact;

        if (payload.HasVerifiedToolFinding || payload.HasAcceptedSubAgentFinding || payload.IsCompactionBoundary)
            return MemoryClass.Evidence;

        return MemoryClass.DurableFact;
    }

    private static bool IsEphemeral(string content)
    {
        var lowered = content.ToLowerInvariant();
        return lowered is "ok" or "thanks" or "thank you" or "sounds good";
    }

    private static MemoryKind ResolveKind(MemoryCheckpointPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Kind))
        {
            if (MemoryDomainEnumExtensions.TryFromWireValue(payload.Kind, out MemoryKind parsed))
                return parsed;
            return MemoryKind.Document;
        }

        if (payload.HasVerifiedToolFinding || payload.TriggerType.Contains("tool", StringComparison.OrdinalIgnoreCase))
            return MemoryKind.Record;

        return MemoryKind.Document;
    }

    private static MemoryUpdateSemantics ResolveUpdateSemantics(MemoryCheckpointPayload payload, MemoryKind kind, MemoryClass memoryClass)
    {
        if (!string.IsNullOrWhiteSpace(payload.UpdateSemantics)
            && MemoryDomainEnumExtensions.TryFromWireValue(payload.UpdateSemantics, out MemoryUpdateSemantics parsed))
            return parsed;

        if (payload.Delete)
            return MemoryUpdateSemantics.Tombstone;

        if (memoryClass == MemoryClass.Trace)
            return MemoryUpdateSemantics.ConversationTrace;

        return kind == MemoryKind.Record ? MemoryUpdateSemantics.ImmutableRecord : MemoryUpdateSemantics.MergeDocument;
    }

    private static MemoryRecallMode ResolveRecallMode(MemoryCheckpointPayload payload, MemoryClass memoryClass)
    {
        // Compaction-boundary memories are whole-session summary blobs. They
        // lexically match almost any query in the session's topic area, so when
        // auto-recallable they dominate the candidate pool and crowd out atomic
        // memories. Keep the record (Manual) but out of the automatic recall pool.
        // The summary compaction itself relies on lives in the SessionCompacted
        // event / in-session history — a separate path that is unaffected.
        if (payload.IsCompactionBoundary)
            return MemoryRecallMode.Manual;

        if (memoryClass == MemoryClass.Trace)
            return MemoryRecallMode.Never;

        if (memoryClass == MemoryClass.Evidence)
            return MemoryRecallMode.Searchable;

        if (MemoryDomainEnumExtensions.TryFromWireValue(payload.RecallMode, out MemoryRecallMode parsed))
            return parsed;

        return MemoryRecallMode.Auto;
    }

    private static long? ResolveExpiry(MemoryCheckpointPayload payload, MemoryClass memoryClass)
    {
        var freshnessAt = payload.FreshnessAtMs;
        if (!freshnessAt.HasValue)
            return null;

        return memoryClass switch
        {
            MemoryClass.Evidence => freshnessAt.Value + (long)MemoryExpiryDefaults.EvidenceExpiry.TotalMilliseconds,
            MemoryClass.Trace => freshnessAt.Value + (long)MemoryExpiryDefaults.TraceExpiry.TotalMilliseconds,
            _ => null
        };
    }

    private static string ResolveTitle(MemoryCheckpointPayload payload, MemoryKind kind, string content)
    {
        if (!string.IsNullOrWhiteSpace(payload.Title))
            return payload.Title;

        if (kind == MemoryKind.Record)
            return payload.TriggerType;

        return content.Length <= 72 ? content : content[..72];
    }

    private static string ResolveAnchor(string content, SessionId sessionId)
    {
        var firstWord = content
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstWord))
            return firstWord.ToLowerInvariant();

        var value = sessionId.Value;
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? value[..slash].ToLowerInvariant() : "session";
    }

    public static string BuildFingerprint(string kind, string title, string content)
    {
        return $"{kind}|{title.Trim().ToLowerInvariant()}|{content.Trim().ToLowerInvariant()}";
    }
}

public sealed class MemoryCurationEngine(
    SQLiteMemoryStore store,
    MemoryRulesFirstExtractor rules,
    MemoryConfig memoryConfig,
    ILogger<MemoryCurationEngine>? logger = null,
    MemoryEmbedderHolder? embedderHolder = null,
    MemoryVectorIndexHolder? vectorIndexHolder = null)
{
    private const string CheckpointDroppedEvent = "memory_checkpoint_dropped_before_curation";
    private const string CheckpointDroppedTemplate =
        CheckpointDroppedEvent +
        " CheckpointId={CheckpointId} SessionId={SessionId} TriggerType={TriggerType}" +
        " IsExplicitRequest={IsExplicitRequest} ContentLength={ContentLength}" +
        " UserContentLength={UserContentLength} DropReason={DropReason} DropDetail={DropDetail}";

    private readonly ILogger<MemoryCurationEngine> _logger = logger ?? NullLogger<MemoryCurationEngine>.Instance;

    // Same evaluator the inline per-session actor uses (memory-core-redesign Slice 1),
    // constructed with no LLM client: the daemon checkpoint worker has none to give it
    // today, so every Ambiguous decision here resolves via
    // CurationRulesEvaluator.TryAutoResolveAmbiguous rather than escalating. This is the
    // one place the daemon path previously performed no dedup/relationship evaluation at
    // all beyond the fingerprint check below — routing through the shared evaluator is
    // what makes GuardDestructiveUpdate (previously inline-actor-only; audit finding D14)
    // apply here too.
    //
    // embedderHolder/vectorIndexHolder ARE wired here (memory-core-redesign Slice 3 Stage B,
    // task 3.1): the embedding kNN nominator runs on this pipeline too, even with no LLM
    // client — a nominee found with no LLM available forces the conservative no-auto-merge
    // Create outcome documented on MemoryCurationEvaluator.EvaluateAsync, never a silent
    // auto-skip/auto-merge on cosine alone.
    private readonly MemoryCurationEvaluator _evaluator =
        new(store, (ILogger)(logger ?? NullLogger<MemoryCurationEngine>.Instance), memoryConfig.Curation,
            llmClient: null, embedderHolder, vectorIndexHolder);

    public async Task<IReadOnlyList<SQLiteMemoryCurationOperation>> CurateAsync(
        SQLiteMemoryCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        MemoryCheckpointPayload? payload;
        try
        {
            if (checkpoint.TriggerType == CheckpointTriggerType.ObservedMemoryProposals.ToWireValue())
            {
                var observed = JsonSerializer.Deserialize<ObservedMemoryCheckpointPayload>(checkpoint.PayloadJson);
                var observedOps = observed?.Operations ?? [];
                if (observedOps.Count == 0)
                    LogCheckpointDropped(checkpoint, MemoryExtractionDropReason.ObservedProposalsEmpty);
                return observedOps;
            }

            payload = JsonSerializer.Deserialize<MemoryCheckpointPayload>(checkpoint.PayloadJson);
        }
        catch (JsonException ex)
        {
            LogCheckpointDropped(checkpoint, MemoryExtractionDropReason.PayloadDeserializationFailed, exception: ex);
            return [];
        }

        if (payload is null)
        {
            LogCheckpointDropped(checkpoint, MemoryExtractionDropReason.PayloadDeserializationFailed);
            return [];
        }

        var resolvedAudienceWire = MemoryPolicyEvaluator.ResolveAudience(payload.Audience, TrustAudience.Public);
        SecurityPolicyDefaults.TryParseAudience(resolvedAudienceWire, out var parsedAudience);
        var resolvedBoundary = !string.IsNullOrWhiteSpace(payload.Boundary)
            ? payload.Boundary!
            : TrustBoundary.TrustedInstanceValue;

        var existing = await store.SearchMemoriesAsync(payload.Content, 8, resolvedBoundary, parsedAudience, ct);
        var fingerprints = existing
            .Select(x => MemoryRulesFirstExtractor.BuildFingerprint(x.Kind, x.Title, x.Snippet))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extraction = rules.ExtractWithDiagnostics(payload, fingerprints);
        var candidates = extraction.Candidates;
        if (candidates.Count == 0)
        {
            LogCheckpointDropped(checkpoint, extraction.DropReason, payload, extraction.DropDetail);
            return [];
        }

        var operations = candidates.Select(c => new SQLiteMemoryCurationOperation(
            Kind: c.Kind.ToWireValue(),
            MemoryClass: c.MemoryClass.ToWireValue(),
            MemoryId: c.MemoryId,
            AnchorCanonicalName: c.AnchorCanonicalName,
            AnchorType: c.AnchorType,
            Title: c.Title,
            Content: c.Content,
            Relations: null,
            UpdateSemantics: c.UpdateSemantics.ToWireValue(),
            Boundary: c.Boundary,
            Audience: c.Audience,
            Sensitivity: c.Sensitivity,
            RecallMode: c.RecallMode.ToWireValue(),
            Confidence: c.Confidence,
            AliasesJson: c.AliasesJson,
            FacetsJson: c.FacetsJson,
            SlotsJson: c.SlotsJson,
            FreshnessAtMs: c.FreshnessAtMs,
            ExpiresAtMs: c.ExpiresAtMs,
            SupersedesRecordId: c.SupersedesRecordId)).ToArray();

        return await EvaluateAndApplyAsync(operations, checkpoint, ct);
    }

    /// <summary>
    /// Runs each extracted candidate through the shared evaluator (dedup/relationship
    /// decision, then the decision-to-write-operation mapping) before the caller persists
    /// the result via <see cref="SQLiteMemoryStore.ApplyCurationBatchAsync"/> — the same
    /// evaluate-then-apply sequence the inline actor's write phase runs. A Skip decision
    /// drops the candidate here rather than at the caller.
    /// </summary>
    private async Task<IReadOnlyList<SQLiteMemoryCurationOperation>> EvaluateAndApplyAsync(
        IReadOnlyList<SQLiteMemoryCurationOperation> operations,
        SQLiteMemoryCheckpoint checkpoint,
        CancellationToken ct)
    {
        if (operations.Count == 0)
            return operations;

        var sessionId = (SessionId)checkpoint.SessionId;
        var results = new List<SQLiteMemoryCurationOperation>(operations.Count);

        foreach (var operation in operations)
        {
            var evaluation = await _evaluator.EvaluateAsync(operation, sessionId, ct);
            var writeOp = await _evaluator.ApplyDecisionAsync(operation, evaluation.Decision, evaluation.Candidates, ct);
            if (writeOp is not null)
                results.Add(writeOp);
        }

        return results;
    }

    private void LogCheckpointDropped(
        SQLiteMemoryCheckpoint checkpoint,
        MemoryExtractionDropReason reason,
        MemoryCheckpointPayload? payload = null,
        string? detail = null,
        Exception? exception = null)
    {
        var level = exception is not null ? LogLevel.Warning : LogLevel.Information;
        if (!_logger.IsEnabled(level))
            return;

        _logger.Log(
            level,
            exception,
            CheckpointDroppedTemplate,
            checkpoint.CheckpointId,
            checkpoint.SessionId,
            checkpoint.TriggerType,
            payload?.IsExplicitRequest,
            payload?.Content?.Length,
            payload?.UserContent?.Length,
            reason,
            detail ?? string.Empty);
    }
}
