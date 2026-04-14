using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    TurnCompleteNoProjectFact,
    TraceNotExplicit,
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
    private static readonly TimeSpan EvidenceExpiry = TimeSpan.FromDays(30);
    private static readonly TimeSpan TraceExpiry = TimeSpan.FromHours(72);
    private static readonly Regex ProjectStatementPattern = new(
        "^(?<subject>(?:[A-Z][A-Za-z0-9.+-]*)(?:\\s+[A-Z][A-Za-z0-9.+-]*){0,4}|(?:our|the)\\s+[a-z][a-z0-9_-]*(?:\\s+[a-z][a-z0-9_-]*){0,4})\\s+(?<verb>has|have|uses|use|supports|support|requires|require|needs|need|completed|completes)\\s+(?<object>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CompletedStatementPattern = new(
        "^we\\s+(?:successfully\\s+)?completed\\s+(?<object>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

        if (MemoryDomainEnumExtensions.TryFromWireValue(payload.TriggerType, out CheckpointTriggerType trigger)
            && trigger == CheckpointTriggerType.TurnComplete
            && !payload.IsExplicitRequest)
        {
            var promoted = TryExtractProjectOperatingFact(payload, fingerprintSet);
            if (promoted is not null)
                results.Add(promoted);

            return results.Count > 0
                ? new MemoryExtractionResult(results, MemoryExtractionDropReason.None)
                : new MemoryExtractionResult(results, MemoryExtractionDropReason.TurnCompleteNoProjectFact);
        }

        var memoryClass = ResolveMemoryClass(payload);
        if (memoryClass == MemoryClass.Trace && !payload.IsExplicitRequest)
            return new MemoryExtractionResult(results, MemoryExtractionDropReason.TraceNotExplicit);

        var resolvedAudienceWire = MemoryPolicyEvaluator.ResolveAudience(payload.Audience, TrustAudience.Public);
        SecurityPolicyDefaults.TryParseAudience(resolvedAudienceWire, out var parsedAudience);
        var resolvedBoundary = MemoryPolicyScopeResolver.ResolveBoundary(payload.Boundary);

        var kind = ResolveKind(payload);
        var title = ResolveTitle(payload, kind, content);
        var updateSemantics = ResolveUpdateSemantics(payload, kind, memoryClass);
        var anchor = ResolveAnchor(content, payload.SessionId);
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

        if (string.Equals(payload.TriggerType, CheckpointTriggerType.TurnComplete.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            return MemoryClass.Trace;

        return MemoryClass.DurableFact;
    }

    private static bool IsEphemeral(string content)
    {
        var lowered = content.ToLowerInvariant();
        return lowered is "ok" or "thanks" or "thank you" or "sounds good";
    }

    private static MemoryKind ResolveKind(MemoryCheckpointPayload payload)
    {
        if (string.Equals(payload.TriggerType, CheckpointTriggerType.TurnComplete.ToWireValue(), StringComparison.OrdinalIgnoreCase)
            && !payload.IsExplicitRequest)
            return MemoryKind.Record;

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
            MemoryClass.Evidence => freshnessAt.Value + (long)EvidenceExpiry.TotalMilliseconds,
            MemoryClass.Trace => freshnessAt.Value + (long)TraceExpiry.TotalMilliseconds,
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

    public static string BuildFingerprint(string kind, string title, string content)
    {
        return $"{kind}|{title.Trim().ToLowerInvariant()}|{content.Trim().ToLowerInvariant()}";
    }

    private static MemoryCheckpointCandidate? TryExtractProjectOperatingFact(
        MemoryCheckpointPayload payload,
        IReadOnlySet<string> fingerprintSet)
    {
        var userText = payload.UserContent?.Trim();
        if (string.IsNullOrWhiteSpace(userText) || IsEphemeral(userText))
            return null;

        if (TryMatchCompletedStatement(userText, out var completedCandidate))
        {
            var completedFingerprint = BuildFingerprint(completedCandidate.Kind.ToWireValue(), completedCandidate.Title, completedCandidate.Content);
            return fingerprintSet.Contains(completedFingerprint) ? null : completedCandidate;
        }

        if (!TryMatchProjectStatement(
                userText,
                payload.FreshnessAtMs,
                payload.Boundary,
                payload.Audience,
                out var candidate))
            return null;

        var fingerprint = BuildFingerprint(candidate.Kind.ToWireValue(), candidate.Title, candidate.Content);
        return fingerprintSet.Contains(fingerprint) ? null : candidate;

        bool TryMatchCompletedStatement(string text, out MemoryCheckpointCandidate matched)
        {
            var completed = CompletedStatementPattern.Match(text);
            if (!completed.Success)
            {
                matched = null!;
                return false;
            }

            var rawObject = CleanStatementTail(completed.Groups["object"].Value);
            if (string.IsNullOrWhiteSpace(rawObject))
            {
                matched = null!;
                return false;
            }

            var normalizedObject = NormalizeSentence(rawObject);
            var title = $"Project Milestone: {SummarizeObject(rawObject)}";
            var milestoneAudienceWire = MemoryPolicyEvaluator.ResolveAudience(payload.Audience, TrustAudience.Public);
            SecurityPolicyDefaults.TryParseAudience(milestoneAudienceWire, out var milestoneAudience);
            matched = new MemoryCheckpointCandidate(
                Kind: MemoryKind.Document,
                MemoryClass: MemoryClass.DurableFact,
                AnchorCanonicalName: Slugify(rawObject),
                AnchorType: "milestone",
                Title: title,
                Content: normalizedObject,
                UpdateSemantics: MemoryUpdateSemantics.MergeDocument,
                Boundary: !string.IsNullOrWhiteSpace(payload.Boundary)
                    ? payload.Boundary!
                    : SecurityPolicyDefaults.TrustedInstanceBoundary,
                Audience: milestoneAudience,
                Sensitivity: payload.Sensitivity,
                RecallMode: MemoryDomainEnumExtensions.TryFromWireValue(payload.RecallMode, out MemoryRecallMode rm)
                    ? rm : MemoryRecallMode.Auto,
                Confidence: Math.Max(payload.Confidence, 0.86),
                AliasesJson: SerializeValues([SummarizeObject(rawObject)]),
                FacetsJson: SerializeValues(["project_fact", "delivery_status"]),
                SlotsJson: null,
                FreshnessAtMs: payload.FreshnessAtMs,
                ExpiresAtMs: null,
                MemoryId: null);
            return true;
        }
    }

    private static bool TryMatchProjectStatement(
        string text,
        long? freshnessAtMs,
        string? boundary,
        string? audience,
        out MemoryCheckpointCandidate candidate)
    {
        var match = ProjectStatementPattern.Match(text);
        if (!match.Success)
        {
            candidate = null!;
            return false;
        }

        var rawSubject = CleanStatementTail(match.Groups["subject"].Value);
        var rawVerb = match.Groups["verb"].Value.Trim().ToLowerInvariant();
        var rawObject = CleanStatementTail(match.Groups["object"].Value);

        if (string.IsNullOrWhiteSpace(rawSubject) || string.IsNullOrWhiteSpace(rawObject))
        {
            candidate = null!;
            return false;
        }

        // Reject conversational fragments that accidentally match the regex
        if (IsConversationalFragment(rawSubject) || IsConversationalFragment(rawObject))
        {
            candidate = null!;
            return false;
        }

        var subjectLabel = NormalizeSubject(rawSubject);
        var objectLabel = SummarizeObject(rawObject);
        var normalizedContent = NormalizeSentence($"{subjectLabel} {NormalizeVerb(rawVerb)} {rawObject}");
        var facet = rawVerb is "requires" or "require" or "needs" or "need"
            ? "product_constraint"
            : "product_capability";
        var slot = rawVerb is "requires" or "require" or "needs" or "need"
            ? "operating_constraint"
            : "product_capability";
        var titlePrefix = rawVerb is "requires" or "require" or "needs" or "need"
            ? "Project Constraint"
            : "Project Fact";

        var stmtAudience = MemoryPolicyScopeResolver.ResolveAudience(audience, sessionId: null);
        candidate = new MemoryCheckpointCandidate(
            Kind: MemoryKind.Document,
            MemoryClass: MemoryClass.DurableFact,
            AnchorCanonicalName: Slugify(rawSubject),
            AnchorType: rawSubject.StartsWith("our ", StringComparison.OrdinalIgnoreCase) || rawSubject.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
                ? "workflow"
                : "project",
            Title: $"{titlePrefix}: {subjectLabel} {NormalizeVerb(rawVerb)} {objectLabel}",
            Content: normalizedContent,
            UpdateSemantics: MemoryUpdateSemantics.MergeDocument,
            Boundary: MemoryPolicyScopeResolver.ResolveBoundary(boundary),
            Audience: stmtAudience,
            Sensitivity: MemorySensitivity.Normal.ToWireValue(),
            RecallMode: MemoryRecallMode.Auto,
            Confidence: 0.88,
            AliasesJson: SerializeValues([subjectLabel, objectLabel]),
            FacetsJson: SerializeValues(["project_fact", facet]),
            SlotsJson: SerializeValues([slot]),
            FreshnessAtMs: freshnessAtMs,
            ExpiresAtMs: null,
            MemoryId: null);
        return true;
    }

    private static readonly string[] ConversationalPrefixes =
    [
        "i ", "well ", "going to ", "want to ", "if that ", "i'm ",
        "you ", "let me ", "maybe ", "just ", "so ", "anyway "
    ];

    private static bool IsConversationalFragment(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        return ConversationalPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal));
    }

    private static int CountSubstantiveWords(string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(w => w.Length >= 3);

    private static string CleanStatementTail(string value)
        => value.Trim().TrimEnd('.', '!', '?');

    private static string NormalizeSubject(string subject)
        => NormalizeSentence(subject.StartsWith("our ", StringComparison.OrdinalIgnoreCase)
            ? subject[4..]
            : subject.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
                ? subject[4..]
                : subject);

    private static string NormalizeVerb(string verb)
        => verb switch
        {
            "have" => "has",
            "use" => "uses",
            "support" => "supports",
            "require" => "requires",
            "need" => "needs",
            _ => verb
        };

    private static string SummarizeObject(string value)
    {
        var cleaned = NormalizeSentence(value);
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length <= 8 ? cleaned : string.Join(' ', words.Take(8));
    }

    private static string NormalizeSentence(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static string Slugify(string value)
    {
        var cleaned = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-");
        return cleaned.Trim('-');
    }

    private static string? SerializeValues(IReadOnlyList<string> values)
    {
        var cleaned = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return cleaned.Length == 0 ? null : JsonSerializer.Serialize(cleaned);
    }
}

public sealed class MemoryCurationEngine(
    SQLiteMemoryStore store,
    MemoryRulesFirstExtractor rules,
    ILogger<MemoryCurationEngine>? logger = null)
{
    private const string CheckpointDroppedEvent = "memory_checkpoint_dropped_before_curation";
    private const string CheckpointDroppedTemplate =
        CheckpointDroppedEvent +
        " CheckpointId={CheckpointId} SessionId={SessionId} TriggerType={TriggerType}" +
        " IsExplicitRequest={IsExplicitRequest} ContentLength={ContentLength}" +
        " UserContentLength={UserContentLength} DropReason={DropReason} DropDetail={DropDetail}";

    private readonly ILogger<MemoryCurationEngine> _logger = logger ?? NullLogger<MemoryCurationEngine>.Instance;

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
            : SecurityPolicyDefaults.TrustedInstanceBoundary;

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

        return candidates.Select(c => new SQLiteMemoryCurationOperation(
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
