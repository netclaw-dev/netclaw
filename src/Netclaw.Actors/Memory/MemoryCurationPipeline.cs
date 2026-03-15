using System.Text.Json;
using System.Text.RegularExpressions;

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
    IReadOnlyList<string>? Evidence = null,
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
    string? AliasesJson,
    string? FacetsJson,
    string? SlotsJson,
    long? FreshnessAtMs,
    long? ExpiresAtMs,
    string? MemoryId,
    string? SupersedesRecordId = null);

public sealed class MemoryRulesFirstExtractor(MemoryPolicyEvaluator policy)
{
    private const string DurableFact = "durable_fact";
    private const string Evidence = "evidence";
    private const string Trace = "trace";
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

        if (string.Equals(payload.TriggerType, "turn-complete", StringComparison.OrdinalIgnoreCase)
            && !payload.IsExplicitRequest)
        {
            var promoted = TryExtractProjectOperatingFact(payload, fingerprintSet);
            if (promoted is not null)
                results.Add(promoted);

            return results;
        }

        var memoryClass = ResolveMemoryClass(payload);
        if (memoryClass == Trace && !payload.IsExplicitRequest)
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
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            FreshnessAtMs: payload.FreshnessAtMs,
            ExpiresAtMs: ResolveExpiry(payload, memoryClass),
            MemoryId: payload.MemoryId,
            SupersedesRecordId: payload.SupersedesRecordId));

        return results;
    }

    private static string ResolveMemoryClass(MemoryCheckpointPayload payload)
    {
        if (payload.IsExplicitRequest ||
            string.Equals(payload.TriggerType, "explicit-memory-request", StringComparison.OrdinalIgnoreCase))
            return DurableFact;

        if (payload.HasVerifiedToolFinding || payload.HasAcceptedSubAgentFinding || payload.IsCompactionBoundary)
            return Evidence;

        if (string.Equals(payload.TriggerType, "turn-complete", StringComparison.OrdinalIgnoreCase))
            return Trace;

        return DurableFact;
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

        if (memoryClass == Trace)
            return "conversation_trace";

        return kind == "record" ? "immutable-record" : "merge-document";
    }

    private static string ResolveRecallMode(MemoryCheckpointPayload payload, string memoryClass)
    {
        if (memoryClass == Trace)
            return "never";

        if (memoryClass == Evidence)
            return "searchable";

        return payload.RecallMode;
    }

    private static long? ResolveExpiry(MemoryCheckpointPayload payload, string memoryClass)
    {
        var freshnessAt = payload.FreshnessAtMs;
        if (!freshnessAt.HasValue)
            return null;

        return memoryClass switch
        {
            Evidence => freshnessAt.Value + (long)EvidenceExpiry.TotalMilliseconds,
            Trace => freshnessAt.Value + (long)TraceExpiry.TotalMilliseconds,
            _ => null
        };
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

    private static MemoryCheckpointCandidate? TryExtractProjectOperatingFact(
        MemoryCheckpointPayload payload,
        IReadOnlySet<string> fingerprintSet)
    {
        if (!payload.Domain.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            return null;

        var userText = payload.UserContent?.Trim();
        if (string.IsNullOrWhiteSpace(userText) || IsEphemeral(userText))
            return null;

        if (TryMatchCompletedStatement(userText, out var completedCandidate))
        {
            var completedFingerprint = BuildFingerprint(completedCandidate.Kind, completedCandidate.Domain, completedCandidate.Title, completedCandidate.Content);
            return fingerprintSet.Contains(completedFingerprint) ? null : completedCandidate;
        }

        if (!TryMatchProjectStatement(userText, payload.Domain, payload.FreshnessAtMs, out var candidate))
            return null;

        var fingerprint = BuildFingerprint(candidate.Kind, candidate.Domain, candidate.Title, candidate.Content);
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
            matched = new MemoryCheckpointCandidate(
                Kind: "document",
                MemoryClass: DurableFact,
                AnchorCanonicalName: Slugify(rawObject),
                AnchorType: "milestone",
                Title: title,
                Content: normalizedObject,
                UpdateSemantics: "merge-document",
                Domain: payload.Domain,
                Sensitivity: payload.Sensitivity,
                RecallMode: payload.RecallMode,
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

    private static bool TryMatchProjectStatement(string text, string domain, long? freshnessAtMs, out MemoryCheckpointCandidate candidate)
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

        candidate = new MemoryCheckpointCandidate(
            Kind: "document",
            MemoryClass: DurableFact,
            AnchorCanonicalName: Slugify(rawSubject),
            AnchorType: rawSubject.StartsWith("our ", StringComparison.OrdinalIgnoreCase) || rawSubject.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
                ? "workflow"
                : "project",
            Title: $"{titlePrefix}: {subjectLabel} {NormalizeVerb(rawVerb)} {objectLabel}",
            Content: normalizedContent,
            UpdateSemantics: "merge-document",
            Domain: domain,
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.88,
            AliasesJson: SerializeValues([subjectLabel, objectLabel]),
            FacetsJson: SerializeValues(["project_fact", facet]),
            SlotsJson: SerializeValues([slot]),
            FreshnessAtMs: freshnessAtMs,
            ExpiresAtMs: null,
            MemoryId: null);
        return true;
    }

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

public sealed class MemoryCurationEngine(SQLiteMemoryStore store, MemoryRulesFirstExtractor rules)
{
    public async Task<IReadOnlyList<SQLiteMemoryCurationOperation>> CurateAsync(
        SQLiteMemoryCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        MemoryCheckpointPayload? payload;
        try
        {
            if (checkpoint.TriggerType == "observed-memory-proposals")
            {
                var observed = JsonSerializer.Deserialize<ObservedMemoryCheckpointPayload>(checkpoint.PayloadJson);
                return observed?.Operations ?? [];
            }

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
            Relations: null,
            UpdateSemantics: c.UpdateSemantics,
            Domain: c.Domain,
            Sensitivity: c.Sensitivity,
            RecallMode: c.RecallMode,
            Confidence: c.Confidence,
            AliasesJson: c.AliasesJson,
            FacetsJson: c.FacetsJson,
            SlotsJson: c.SlotsJson,
            FreshnessAtMs: c.FreshnessAtMs,
            ExpiresAtMs: c.ExpiresAtMs,
            SupersedesRecordId: c.SupersedesRecordId)).ToArray();
    }
}
