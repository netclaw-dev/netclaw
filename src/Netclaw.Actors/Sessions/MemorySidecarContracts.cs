namespace Netclaw.Actors.Sessions;

public enum MemoryProposalOperation
{
    UpsertDocument,
    AppendRecord,
    Ignore
}

public sealed record MemoryProposal(
    string Operation,
    string MemoryClass,
    string SubjectKind,
    string SubjectValue,
    MemoryAnchor? Anchor,
    string Title,
    string Content,
    IReadOnlyList<string>? Aliases,
    IReadOnlyList<string>? Facets,
    IReadOnlyList<string>? Slots,
    IReadOnlyList<MemoryRelation>? Relations,
    string RecallMode,
    string Sensitivity,
    double Confidence,
    long? FreshUntilMs,
    long? ExpiresAtMs,
    string? TargetSurface,
    string? Rationale);

public sealed record RecallPlanningRequest(
    string SessionId,
    string Mode,
    string UserText,
    IReadOnlyList<string> RecentUserTurns,
    IReadOnlyList<string> RecentAssistantTurns,
    IReadOnlyList<string> RecentEntities,
    int MaxQueryTerms,
    int MaxResults);

public sealed record RecallQueryPlan(
    string Mode,
    string Intent,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<string> MemoryClasses,
    int MaxResults,
    bool AllowExpiredEvidence);

public sealed record MemoryAnchor(
    string CanonicalName,
    string AnchorType);

public sealed record MemoryRelation(
    string RelationType,
    MemoryAnchor TargetAnchor);
