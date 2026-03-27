using System.Text.Json;
using System.Text.RegularExpressions;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;

namespace Netclaw.Actors.Memory;

public sealed class MemoryProposalGate
{
    private static readonly string[] StableIdentityFacets =
    [
        "travel_profile",
        "personal_profile",
        "household_profile",
        "pet_profile"
    ];

    private static readonly string[] StableIdentityAnchorTypes =
    [
        "preference",
        "profile",
        "pet",
        "location"
    ];

    public sealed record ProposalDecisionSummary(
        int Total,
        int Accepted,
        int IdentityUpdates,
        IReadOnlyDictionary<string, int> RejectionReasons);

    private static readonly TimeSpan EvidenceExpiry = TimeSpan.FromDays(30);
    private static readonly TimeSpan TraceExpiry = TimeSpan.FromHours(72);
    private static readonly Regex IdentityTitlePattern = new(
        "\\b(name|tone|style|voice|persona|communication preference|response preference)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VolatileIdentityPattern = new(
        "\\b(today|tonight|tomorrow|this week|this month|right now|currently)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<SQLiteMemoryCurationOperation> Accept(
        IReadOnlyList<MemoryProposal> proposals,
        string domain,
        string defaultSensitivity,
        long nowMs,
        string? boundary = null,
        TrustAudience audience = TrustAudience.Public)
        => Evaluate(proposals, domain, defaultSensitivity, nowMs, boundary, audience).MemoryOperations;

    public MemoryProposalGateResult Evaluate(
        IReadOnlyList<MemoryProposal> proposals,
        string domain,
        string defaultSensitivity,
        long nowMs,
        string? boundary = null,
        TrustAudience audience = TrustAudience.Public)
    {
        var accepted = new List<SQLiteMemoryCurationOperation>();
        var identityUpdates = new List<IdentityProfileUpdate>();
        var rejectionReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var proposal in proposals)
        {
            if (proposal is null)
            {
                CountReject("null-proposal");
                continue;
            }

            if (!MemoryDomainEnumExtensions.TryFromWireValue(proposal.Operation, out MemoryProposalOperation operation)
                || operation == MemoryProposalOperation.Ignore)
            {
                CountReject("invalid-operation");
                continue;
            }

            if (!MemoryDomainEnumExtensions.TryFromWireValue(proposal.MemoryClass, out MemoryClass memoryClass))
            {
                CountReject("invalid-memory-class");
                continue;
            }

            if (!HasRequiredRetrievalMetadata(proposal, memoryClass))
            {
                CountReject("missing-retrieval-metadata");
                continue;
            }

            if (string.Equals(proposal.TargetSurface, "identity_profile", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsIdentityEligible(proposal, memoryClass))
                {
                    CountReject("invalid-identity-surface");
                    continue;
                }

                identityUpdates.Add(new IdentityProfileUpdate(
                    proposal.Title,
                    proposal.Content,
                    proposal.Rationale));

                var mirrorOperation = TryBuildIdentityMirrorOperation(proposal, operation, memoryClass, domain, defaultSensitivity, nowMs, boundary, audience);
                if (mirrorOperation is not null)
                    accepted.Add(mirrorOperation);

                continue;
            }

            var sensitivity = string.IsNullOrWhiteSpace(proposal.Sensitivity)
                ? defaultSensitivity
                : proposal.Sensitivity;

            var recallMode = ResolveRecallMode(memoryClass, sensitivity);
            var freshnessAt = proposal.FreshUntilMs ?? nowMs;
            var expiry = ResolveExpiry(memoryClass, proposal.ExpiresAtMs, freshnessAt);
            var content = proposal.Content;

            if (memoryClass is MemoryClass.Evidence or MemoryClass.Trace)
            {
                var envelope = new EvidenceEnvelope(
                    proposal.SubjectKind,
                    proposal.SubjectValue,
                    proposal.PredicateOrFallback(),
                    proposal.ObjectOrContentFallback(),
                    proposal.Rationale,
                    expiry,
                    freshnessAt);
                content = JsonSerializer.Serialize(envelope);
            }

            accepted.Add(BuildMemoryOperation(proposal, operation, memoryClass, domain, sensitivity, recallMode, freshnessAt, expiry, content, boundary, audience));
        }

        return new MemoryProposalGateResult(
            accepted,
            identityUpdates,
            new ProposalDecisionSummary(
                Total: proposals.Count,
                Accepted: accepted.Count,
                IdentityUpdates: identityUpdates.Count,
                RejectionReasons: rejectionReasons));

        void CountReject(string reason)
            => rejectionReasons[reason] = rejectionReasons.TryGetValue(reason, out var current) ? current + 1 : 1;
    }

    private static MemoryRecallMode ResolveRecallMode(MemoryClass memoryClass, string sensitivity)
    {
        if (MemoryDomainEnumExtensions.TryFromWireValue(sensitivity, out MemorySensitivity sens)
            && sens == MemorySensitivity.Secret)
            return MemoryRecallMode.Never;

        return memoryClass switch
        {
            MemoryClass.DurableFact => MemoryRecallMode.Auto,
            MemoryClass.Evidence => MemoryRecallMode.Searchable,
            _ => MemoryRecallMode.Never
        };
    }

    private static long? ResolveExpiry(MemoryClass memoryClass, long? proposalExpiry, long freshnessAt)
    {
        if (proposalExpiry.HasValue)
            return proposalExpiry;

        return memoryClass switch
        {
            MemoryClass.Evidence => freshnessAt + (long)EvidenceExpiry.TotalMilliseconds,
            MemoryClass.Trace => freshnessAt + (long)TraceExpiry.TotalMilliseconds,
            _ => null
        };
    }

    private static bool IsIdentityEligible(MemoryProposal proposal, MemoryClass memoryClass)
    {
        if (memoryClass != MemoryClass.DurableFact)
            return false;

        MemoryDomainEnumExtensions.TryFromWireValue(proposal.SubjectKind, out SubjectKind subjectKind);
        if (subjectKind is not (SubjectKind.User or SubjectKind.Assistant or SubjectKind.Agent))
            return false;

        var title = proposal.Title ?? string.Empty;
        var rationale = proposal.Rationale ?? string.Empty;
        if (IdentityTitlePattern.IsMatch(title) || IdentityTitlePattern.IsMatch(rationale))
            return true;

        if (subjectKind != SubjectKind.User)
            return false;

        var facets = proposal.Facets ?? [];
        if (facets.Any(f => StableIdentityFacets.Contains(f, StringComparer.OrdinalIgnoreCase)))
            return true;

        return proposal.Anchor is not null
            && StableIdentityAnchorTypes.Contains(proposal.Anchor.AnchorType, StringComparer.OrdinalIgnoreCase);
    }

    private static SQLiteMemoryCurationOperation? TryBuildIdentityMirrorOperation(
        MemoryProposal proposal,
        MemoryProposalOperation operation,
        MemoryClass memoryClass,
        string domain,
        string defaultSensitivity,
        long nowMs,
        string? boundary,
        TrustAudience audience)
    {
        if (!MemoryDomainEnumExtensions.TryFromWireValue(proposal.SubjectKind, out SubjectKind subjectKind)
            || subjectKind != SubjectKind.User)
            return null;

        var facets = proposal.Facets ?? [];
        if (!facets.Any(f => StableIdentityFacets.Contains(f, StringComparer.OrdinalIgnoreCase)))
            return null;

        var identityText = string.Join(" ", new[] { proposal.Title, proposal.Content, proposal.Rationale }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (VolatileIdentityPattern.IsMatch(identityText))
            return null;

        var sensitivity = string.IsNullOrWhiteSpace(proposal.Sensitivity)
            ? defaultSensitivity
            : proposal.Sensitivity;

        if (MemoryDomainEnumExtensions.TryFromWireValue(sensitivity, out MemorySensitivity sens)
            && sens == MemorySensitivity.Secret)
            return null;

        var freshnessAt = proposal.FreshUntilMs ?? nowMs;
        var expiry = ResolveExpiry(memoryClass, proposal.ExpiresAtMs, freshnessAt);
        var recallMode = ResolveRecallMode(memoryClass, sensitivity);
        return BuildMemoryOperation(proposal, operation, memoryClass, domain, sensitivity, recallMode, freshnessAt, expiry, proposal.Content, boundary, audience);
    }

    private static SQLiteMemoryCurationOperation BuildMemoryOperation(
        MemoryProposal proposal,
        MemoryProposalOperation operation,
        MemoryClass memoryClass,
        string domain,
        string sensitivity,
        MemoryRecallMode recallMode,
        long freshnessAt,
        long? expiry,
        string content,
        string? boundary,
        TrustAudience audience)
    {
        // Policy override: memoryClass determines storage kind, not the LLM's operation choice.
        // Evidence and trace are always immutable records; durable_fact is always a document.
        var kind = memoryClass switch
        {
            MemoryClass.Evidence => MemoryKind.Record,
            MemoryClass.Trace => MemoryKind.Record,
            _ => MemoryKind.Document
        };
        var updateSemantics = memoryClass switch
        {
            MemoryClass.Evidence => MemoryUpdateSemantics.ImmutableRecord,
            MemoryClass.Trace => MemoryUpdateSemantics.ConversationTrace,
            _ => MemoryUpdateSemantics.MergeDocument
        };

        return new SQLiteMemoryCurationOperation(
            Kind: kind.ToWireValue(),
            MemoryClass: memoryClass.ToWireValue(),
            MemoryId: null,
            AnchorCanonicalName: string.IsNullOrWhiteSpace(proposal.Anchor?.CanonicalName)
                ? (string.IsNullOrWhiteSpace(proposal.SubjectValue) ? proposal.Title : proposal.SubjectValue)
                : proposal.Anchor.CanonicalName,
            AnchorType: string.IsNullOrWhiteSpace(proposal.Anchor?.AnchorType)
                ? (string.IsNullOrWhiteSpace(proposal.SubjectKind) ? "concept" : proposal.SubjectKind)
                : proposal.Anchor.AnchorType,
            Title: proposal.Title,
            Content: content,
            AliasesJson: SerializeStringList(proposal.Aliases),
            FacetsJson: SerializeStringList(proposal.Facets),
            SlotsJson: SerializeSlots(proposal, memoryClass),
            Relations: BuildRelations(proposal, memoryClass),
            UpdateSemantics: updateSemantics.ToWireValue(),
            Domain: domain,
            Boundary: MemoryPolicyScopeResolver.ResolveBoundary(boundary, audience, sessionId: null, domain),
            Audience: audience.ToWireValue(),
            Sensitivity: sensitivity,
            RecallMode: recallMode.ToWireValue(),
            Confidence: Math.Clamp(proposal.Confidence, 0.0, 1.0),
            FreshnessAtMs: freshnessAt,
            ExpiresAtMs: expiry,
            SupersedesRecordId: null);
    }

    private static bool HasRequiredRetrievalMetadata(MemoryProposal proposal, MemoryClass memoryClass)
    {
        if (memoryClass == MemoryClass.Trace)
            return true;

        if (proposal.Anchor is null || string.IsNullOrWhiteSpace(proposal.Anchor.CanonicalName) || string.IsNullOrWhiteSpace(proposal.Anchor.AnchorType))
            return false;

        var aliases = proposal.Aliases ?? [];
        var facets = proposal.Facets ?? [];
        return aliases.Count > 0 && facets.Count > 0;
    }

    private static string? SerializeStringList(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return null;

        var cleaned = values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return cleaned.Length == 0 ? null : JsonSerializer.Serialize(cleaned);
    }

    private static string? SerializeSlots(MemoryProposal proposal, MemoryClass memoryClass)
    {
        if (memoryClass != MemoryClass.DurableFact)
            return null;

        return SerializeStringList(proposal.Slots);
    }

    private static IReadOnlyList<SQLiteMemoryRelationOperation>? BuildRelations(MemoryProposal proposal, MemoryClass memoryClass)
    {
        if (memoryClass != MemoryClass.DurableFact || proposal.Confidence < 0.9)
            return null;

        var relations = proposal.Relations ?? [];
        var accepted = relations
            .Where(r => r is not null)
            .Where(r => !string.IsNullOrWhiteSpace(r.RelationType))
            .Where(r => r.TargetAnchor is not null
                && !string.IsNullOrWhiteSpace(r.TargetAnchor.CanonicalName)
                && !string.IsNullOrWhiteSpace(r.TargetAnchor.AnchorType))
            .Take(3)
            .Select(r => new SQLiteMemoryRelationOperation(
                RelationType: r.RelationType.Trim(),
                TargetCanonicalName: r.TargetAnchor.CanonicalName.Trim(),
                TargetAnchorType: r.TargetAnchor.AnchorType.Trim(),
                Confidence: Math.Clamp(proposal.Confidence, 0.0, 1.0)))
            .ToArray();

        return accepted.Length == 0 ? null : accepted;
    }

    private sealed record EvidenceEnvelope(
        string SubjectKind,
        string SubjectValue,
        string Predicate,
        string ObjectValue,
        string? Rationale,
        long? ExpiresAtMs,
        long? FreshUntilMs);
}

public sealed record IdentityProfileUpdate(
    string Title,
    string Content,
    string? Rationale);

public sealed record MemoryProposalGateResult(
    IReadOnlyList<SQLiteMemoryCurationOperation> MemoryOperations,
    IReadOnlyList<IdentityProfileUpdate> IdentityUpdates,
    MemoryProposalGate.ProposalDecisionSummary Summary);

public sealed class RecallPlanGate
{
    public RecallQueryPlan Clamp(RecallQueryPlan? plan, RecallPlanningRequest request)
    {
        var fallbackTerms = request.UserText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(request.MaxQueryTerms)
            .ToArray();

        if (plan is null)
        {
            return new RecallQueryPlan(
                request.Mode,
                "fallback",
                [],
                [],
                fallbackTerms,
                request.Mode == "intentional"
                    ? [MemoryClass.DurableFact.ToWireValue(), MemoryClass.Evidence.ToWireValue()]
                    : [MemoryClass.DurableFact.ToWireValue()],
                request.MaxResults,
                false);
        }

        var durableFactWire = MemoryClass.DurableFact.ToWireValue();
        var evidenceWire = MemoryClass.Evidence.ToWireValue();

        var classes = request.Mode == "intentional"
            ? plan.MemoryClasses.Where(c => string.Equals(c, durableFactWire, StringComparison.OrdinalIgnoreCase) || string.Equals(c, evidenceWire, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(durableFactWire).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [durableFactWire];

        var searchTerms = plan.SearchTerms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, request.MaxQueryTerms))
            .ToArray();

        if (searchTerms.Length == 0)
            searchTerms = fallbackTerms;

        return plan with
        {
            Mode = request.Mode,
            MemoryClasses = classes,
            SearchTerms = searchTerms,
            MaxResults = Math.Clamp(plan.MaxResults, 1, request.MaxResults),
            AllowExpiredEvidence = request.Mode == "intentional" && plan.AllowExpiredEvidence
        };
    }
}

internal static class MemoryProposalExtensions
{
    public static string PredicateOrFallback(this MemoryProposal proposal)
        => string.IsNullOrWhiteSpace(proposal.Title) ? "supports" : proposal.Title;

    public static string ObjectOrContentFallback(this MemoryProposal proposal)
        => string.IsNullOrWhiteSpace(proposal.Content) ? proposal.SubjectValue : proposal.Content;
}
