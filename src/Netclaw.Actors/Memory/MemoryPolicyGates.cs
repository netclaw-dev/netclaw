using System.Text.Json;
using System.Text.RegularExpressions;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Memory;

public sealed class MemoryProposalGate
{
    private static readonly TimeSpan EvidenceExpiry = TimeSpan.FromDays(30);
    private static readonly TimeSpan TraceExpiry = TimeSpan.FromHours(72);
    private static readonly Regex IdentityTitlePattern = new(
        "\\b(name|tone|style|voice|persona|communication preference|response preference)\\b",
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

        foreach (var proposal in proposals)
        {
            if (proposal is null)
                continue;

            if (proposal.Operation is not ("upsert_document" or "append_record"))
                continue;

            if (proposal.MemoryClass is not ("durable_fact" or "evidence" or "trace"))
                continue;

            if (!HasRequiredRetrievalMetadata(proposal))
                continue;

            if (string.Equals(proposal.TargetSurface, "identity_profile", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsIdentityEligible(proposal))
                    continue;

                identityUpdates.Add(new IdentityProfileUpdate(
                    proposal.Title,
                    proposal.Content,
                    proposal.Rationale));
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

            accepted.Add(new SQLiteMemoryCurationOperation(
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
                UpdateSemantics: proposal.MemoryClass == "trace"
                    ? "conversation_trace"
                    : proposal.Operation == "append_record" ? "immutable-record" : "merge-document",
                Domain: domain,
                Sensitivity: sensitivity,
                RecallMode: recallMode,
                Confidence: Math.Clamp(proposal.Confidence, 0.0, 1.0),
                FreshnessAtMs: freshnessAt,
                ExpiresAtMs: expiry,
                SupersedesRecordId: null));
        }

        return new MemoryProposalGateResult(accepted, identityUpdates);
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

        if (!string.Equals(proposal.SubjectKind, "user", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(proposal.SubjectKind, "assistant", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(proposal.SubjectKind, "agent", StringComparison.OrdinalIgnoreCase))
            return false;

        var title = proposal.Title ?? string.Empty;
        var rationale = proposal.Rationale ?? string.Empty;
        return IdentityTitlePattern.IsMatch(title) || IdentityTitlePattern.IsMatch(rationale);
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
    IReadOnlyList<IdentityProfileUpdate> IdentityUpdates);

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
