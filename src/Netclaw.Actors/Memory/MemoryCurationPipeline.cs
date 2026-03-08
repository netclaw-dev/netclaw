using System.Text.Json;

namespace Netclaw.Actors.Memory;

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
    string Domain,
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
    string? SupersedesRecordId = null,
    long? FreshnessAtMs = null);

public sealed record MemoryCheckpointCandidate(
    string Kind,
    string MemoryClass,
    string AnchorCanonicalName,
    string AnchorType,
    string Title,
    string Content,
    string UpdateSemantics,
    string Domain,
    string Sensitivity,
    string RecallMode,
    double Confidence,
    long? FreshnessAtMs,
    string? MemoryId,
    string? SupersedesRecordId = null);

public sealed class MemoryRulesFirstExtractor(MemoryPolicyEvaluator policy)
{
    private const string DurableExplicit = "durable_explicit";
    private const string DurableInferred = "durable_inferred";
    private const string ConversationTrace = "conversation_trace";

    public IReadOnlyList<MemoryCheckpointCandidate> Extract(
        MemoryCheckpointPayload payload,
        IReadOnlySet<string> fingerprintSet)
    {
        var results = new List<MemoryCheckpointCandidate>();

        var content = payload.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            return results;

        if (IsEphemeral(content))
            return results;

        var decision = policy.EvaluateWrite(
            payload.Domain,
            payload.Sensitivity,
            payload.RecallMode,
            payload.Confidence,
            payload.IsExplicitRequest);
        if (!decision.Allowed)
            return results;

        var memoryClass = ResolveMemoryClass(payload);
        if (memoryClass == ConversationTrace && !payload.IsExplicitRequest)
            return results;

        var kind = ResolveKind(payload);
        var title = ResolveTitle(payload, kind, content);
        var updateSemantics = ResolveUpdateSemantics(payload, kind, memoryClass);
        var anchor = ResolveAnchor(content, payload.SessionId);
        var anchorType = kind == "record" ? "event" : "concept";
        var fingerprint = BuildFingerprint(kind, payload.Domain, title, content);
        if (fingerprintSet.Contains(fingerprint))
            return results;

        results.Add(new MemoryCheckpointCandidate(
            Kind: kind,
            MemoryClass: memoryClass,
            AnchorCanonicalName: anchor,
            AnchorType: anchorType,
            Title: title,
            Content: content,
            UpdateSemantics: updateSemantics,
            Domain: payload.Domain,
            Sensitivity: payload.Sensitivity,
            RecallMode: ResolveRecallMode(payload, memoryClass),
            Confidence: payload.Confidence,
            FreshnessAtMs: payload.FreshnessAtMs,
            MemoryId: payload.MemoryId,
            SupersedesRecordId: payload.SupersedesRecordId));

        return results;
    }

    private static string ResolveMemoryClass(MemoryCheckpointPayload payload)
    {
        if (payload.IsExplicitRequest ||
            string.Equals(payload.TriggerType, "explicit-memory-request", StringComparison.OrdinalIgnoreCase))
            return DurableExplicit;

        if (payload.HasVerifiedToolFinding || payload.HasAcceptedSubAgentFinding || payload.IsCompactionBoundary)
            return DurableInferred;

        if (string.Equals(payload.TriggerType, "turn-complete", StringComparison.OrdinalIgnoreCase))
            return ConversationTrace;

        return DurableInferred;
    }

    private static bool IsEphemeral(string content)
    {
        var lowered = content.ToLowerInvariant();
        return lowered is "ok" or "thanks" or "thank you" or "sounds good";
    }

    private static string ResolveKind(MemoryCheckpointPayload payload)
    {
        if (string.Equals(payload.TriggerType, "turn-complete", StringComparison.OrdinalIgnoreCase)
            && !payload.IsExplicitRequest)
            return "record";

        if (!string.IsNullOrWhiteSpace(payload.Kind))
            return payload.Kind;

        if (payload.HasVerifiedToolFinding || payload.TriggerType.Contains("tool", StringComparison.OrdinalIgnoreCase))
            return "record";

        return "document";
    }

    private static string ResolveUpdateSemantics(MemoryCheckpointPayload payload, string kind, string memoryClass)
    {
        if (!string.IsNullOrWhiteSpace(payload.UpdateSemantics))
            return payload.UpdateSemantics;

        if (payload.Delete)
            return "tombstone";

        if (memoryClass == ConversationTrace)
            return ConversationTrace;

        return kind == "record" ? "immutable-record" : "merge-document";
    }

    private static string ResolveRecallMode(MemoryCheckpointPayload payload, string memoryClass)
    {
        if (memoryClass == ConversationTrace)
            return "never";

        return payload.RecallMode;
    }

    private static string ResolveTitle(MemoryCheckpointPayload payload, string kind, string content)
    {
        if (!string.IsNullOrWhiteSpace(payload.Title))
            return payload.Title;

        if (kind == "record")
            return payload.TriggerType;

        return content.Length <= 72 ? content : content[..72];
    }

    private static string ResolveAnchor(string content, string sessionId)
    {
        var firstWord = content
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstWord))
            return firstWord.ToLowerInvariant();

        var slash = sessionId.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? sessionId[..slash].ToLowerInvariant() : "session";
    }

    public static string BuildFingerprint(string kind, string domain, string title, string content)
    {
        return $"{kind}|{domain}|{title.Trim().ToLowerInvariant()}|{content.Trim().ToLowerInvariant()}";
    }
}

public sealed class MemoryCurationEngine(SQLiteMemoryStore store, MemoryRulesFirstExtractor rules)
{
    public async Task<IReadOnlyList<SQLiteMemoryCurationOperation>> CurateAsync(
        SQLiteMemoryCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        MemoryCheckpointPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MemoryCheckpointPayload>(checkpoint.PayloadJson);
        }
        catch
        {
            return [];
        }

        if (payload is null)
            return [];

        var existing = await store.SearchMemoriesAsync(payload.Content, 8, ct);
        var fingerprints = existing
            .Select(x => MemoryRulesFirstExtractor.BuildFingerprint(x.Kind, x.Domain, x.Title, x.Snippet))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = rules.Extract(payload, fingerprints);
        if (candidates.Count == 0)
            return [];

        return candidates.Select(c => new SQLiteMemoryCurationOperation(
            Kind: c.Kind,
            MemoryClass: c.MemoryClass,
            MemoryId: c.MemoryId,
            AnchorCanonicalName: c.AnchorCanonicalName,
            AnchorType: c.AnchorType,
            Title: c.Title,
            Content: c.Content,
            UpdateSemantics: c.UpdateSemantics,
            Domain: c.Domain,
            Sensitivity: c.Sensitivity,
            RecallMode: c.RecallMode,
            Confidence: c.Confidence,
            FreshnessAtMs: c.FreshnessAtMs,
            SupersedesRecordId: c.SupersedesRecordId)).ToArray();
    }
}
