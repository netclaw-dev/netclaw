using System.Text.Json;
using System.Text.RegularExpressions;
using Netclaw.Actors.Sessions;

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
        long nowMs)
        => Evaluate(proposals, domain, defaultSensitivity, nowMs).MemoryOperations;

    public MemoryProposalGateResult Evaluate(
        IReadOnlyList<MemoryProposal> proposals,
        string domain,
        string defaultSensitivity,
        long nowMs)
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

            if (proposal.Operation is not ("upsert_document" or "append_record"))
            {
                CountReject("invalid-operation");
                continue;
            }

            if (proposal.MemoryClass is not ("durable_fact" or "evidence" or "trace"))
            {
                CountReject("invalid-memory-class");
                continue;
            }

            if (!HasRequiredRetrievalMetadata(proposal))
            {
                CountReject("missing-retrieval-metadata");
                continue;
            }

            if (string.Equals(proposal.TargetSurface, "identity_profile", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsIdentityEligible(proposal))
                {
                    CountReject("invalid-identity-surface");
                    continue;
                }

                identityUpdates.Add(new IdentityProfileUpdate(
                    proposal.Title,
                    proposal.Content,
                    proposal.Rationale));

                var mirrorOperation = TryBuildIdentityMirrorOperation(proposal, domain, defaultSensitivity, nowMs);
                if (mirrorOperation is not null)
                    accepted.Add(mirrorOperation);

                continue;
            }

            var sensitivity = string.IsNullOrWhiteSpace(proposal.Sensitivity)
                ? defaultSensitivity
                : proposal.Sensitivity;

            var recallMode = ResolveRecallMode(proposal, sensitivity);
            var freshnessAt = proposal.FreshUntilMs ?? nowMs;
            var expiry = ResolveExpiry(proposal, freshnessAt);
            var content = proposal.Content;

            if (proposal.MemoryClass == "evidence" || proposal.MemoryClass == "trace")
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

            accepted.Add(BuildMemoryOperation(proposal, domain, sensitivity, recallMode, freshnessAt, expiry, content));
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

    private static string ResolveRecallMode(MemoryProposal proposal, string sensitivity)
    {
        if (string.Equals(sensitivity, "secret", StringComparison.OrdinalIgnoreCase))
            return "never";

        return proposal.MemoryClass switch
        {
            "durable_fact" => "auto",
            "evidence" => "searchable",
            _ => "never"
        };
    }

    private static long? ResolveExpiry(MemoryProposal proposal, long freshnessAt)
    {
        if (proposal.ExpiresAtMs.HasValue)
            return proposal.ExpiresAtMs;

        return proposal.MemoryClass switch
        {
            "evidence" => freshnessAt + (long)EvidenceExpiry.TotalMilliseconds,
            "trace" => freshnessAt + (long)TraceExpiry.TotalMilliseconds,
            _ => null
        };
    }

    private static bool IsIdentityEligible(MemoryProposal proposal)
    {
        if (proposal.MemoryClass != "durable_fact")
            return false;

        var isUser = string.Equals(proposal.SubjectKind, "user", StringComparison.OrdinalIgnoreCase);
        var isAssistant = string.Equals(proposal.SubjectKind, "assistant", StringComparison.OrdinalIgnoreCase);
        var isAgent = string.Equals(proposal.SubjectKind, "agent", StringComparison.OrdinalIgnoreCase);
        if (!isUser && !isAssistant && !isAgent)
            return false;

        var title = proposal.Title ?? string.Empty;
        var rationale = proposal.Rationale ?? string.Empty;
        if (IdentityTitlePattern.IsMatch(title) || IdentityTitlePattern.IsMatch(rationale))
            return true;

        if (!isUser)
            return false;

        var facets = proposal.Facets ?? [];
        if (facets.Any(f => StableIdentityFacets.Contains(f, StringComparer.OrdinalIgnoreCase)))
            return true;

        return proposal.Anchor is not null
            && StableIdentityAnchorTypes.Contains(proposal.Anchor.AnchorType, StringComparer.OrdinalIgnoreCase);
    }

    private static SQLiteMemoryCurationOperation? TryBuildIdentityMirrorOperation(
        MemoryProposal proposal,
        string domain,
        string defaultSensitivity,
        long nowMs)
    {
        if (!string.Equals(proposal.SubjectKind, "user", StringComparison.OrdinalIgnoreCase))
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

        if (string.Equals(sensitivity, "secret", StringComparison.OrdinalIgnoreCase))
            return null;

        var freshnessAt = proposal.FreshUntilMs ?? nowMs;
        var expiry = ResolveExpiry(proposal, freshnessAt);
        var recallMode = ResolveRecallMode(proposal, sensitivity);
        return BuildMemoryOperation(proposal, domain, sensitivity, recallMode, freshnessAt, expiry, proposal.Content);
    }

    private static SQLiteMemoryCurationOperation BuildMemoryOperation(
        MemoryProposal proposal,
        string domain,
        string sensitivity,
        string recallMode,
        long freshnessAt,
        long? expiry,
        string content)
    {
        return new SQLiteMemoryCurationOperation(
            Kind: proposal.Operation == "append_record" ? "record" : "document",
            MemoryClass: proposal.MemoryClass,
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
            SlotsJson: SerializeSlots(proposal),
            Relations: BuildRelations(proposal),
            UpdateSemantics: proposal.MemoryClass == "trace"
                ? "conversation_trace"
                : proposal.Operation == "append_record" ? "immutable-record" : "merge-document",
            Domain: domain,
            Sensitivity: sensitivity,
            RecallMode: recallMode,
            Confidence: Math.Clamp(proposal.Confidence, 0.0, 1.0),
            FreshnessAtMs: freshnessAt,
            ExpiresAtMs: expiry,
            SupersedesRecordId: null);
    }

    private static bool HasRequiredRetrievalMetadata(MemoryProposal proposal)
    {
        if (proposal.MemoryClass == "trace")
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

    private static string? SerializeSlots(MemoryProposal proposal)
    {
        if (proposal.MemoryClass != "durable_fact")
            return null;

        return SerializeStringList(proposal.Slots);
    }

    private static IReadOnlyList<SQLiteMemoryRelationOperation>? BuildRelations(MemoryProposal proposal)
    {
        if (proposal.MemoryClass != "durable_fact" || proposal.Confidence < 0.9)
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
                request.Mode == "intentional" ? ["durable_fact", "evidence"] : ["durable_fact"],
                request.MaxResults,
                false);
        }

        var classes = request.Mode == "intentional"
            ? plan.MemoryClasses.Where(c => c is "durable_fact" or "evidence").DefaultIfEmpty("durable_fact").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : ["durable_fact"];

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
