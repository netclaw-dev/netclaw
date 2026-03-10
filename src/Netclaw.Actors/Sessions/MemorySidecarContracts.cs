namespace Netclaw.Actors.Sessions;

public sealed record MemoryObservationRequest(
    string SessionId,
    string TurnId,
    string TriggerType,
    DateTimeOffset ObservedAt,
    MemoryObservationCurrentTurn CurrentTurn,
    MemoryObservationRecentContext RecentContext,
    MemoryObservationPolicyScope PolicyScope);

public sealed record MemoryObservationCurrentTurn(
    string UserSummary,
    string AssistantSummary,
    IReadOnlyList<string> StrongAssertions,
    IReadOnlyList<string> ToolFindingSummaries);

public sealed record MemoryObservationRecentContext(
    string SessionSummary,
    IReadOnlyList<string> RecentUserTurns,
    IReadOnlyList<string> RecentAssistantTurns,
    IReadOnlyList<string> ActiveAnchors);

public sealed record MemoryObservationPolicyScope(
    string Domain,
    string Sensitivity,
    bool IdentityProfileAllowed);

public sealed record MemoryProposal(
    string Operation,
    string MemoryClass,
    string SubjectKind,
    string SubjectValue,
    string Title,
    string Content,
    string RecallMode,
    string Sensitivity,
    double Confidence,
    long? FreshUntilMs,
    long? ExpiresAtMs,
    string? TargetSurface,
    string? Rationale);

public sealed record RecallPlanningRequest(
    string SessionId,
    string Domain,
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

internal sealed record MemoryObservationCompleted
{
    public required IReadOnlyList<MemoryProposal> Proposals { get; init; }
}

internal sealed record RecallPlanningCompleted
{
    public required RecallQueryPlan Plan { get; init; }
}
