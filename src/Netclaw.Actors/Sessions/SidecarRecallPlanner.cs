namespace Netclaw.Actors.Sessions;

public sealed class SidecarRecallPlanner
{
    public RecallPlanningRequest BuildRequest(
        string sessionId,
        string domain,
        string userText,
        IReadOnlyList<string> recentUserTurns,
        IReadOnlyList<string> recentAssistantTurns,
        IReadOnlyList<string> recentEntities,
        string mode,
        int maxQueryTerms,
        int maxResults)
    {
        return new RecallPlanningRequest(
            sessionId,
            domain,
            mode,
            userText,
            recentUserTurns,
            recentAssistantTurns,
            recentEntities,
            maxQueryTerms,
            maxResults);
    }
}

public sealed class SidecarMemoryObserver
{
    public MemoryObservationRequest BuildRequest(
        string sessionId,
        string turnId,
        string triggerType,
        string domain,
        string sensitivity,
        string userSummary,
        string assistantSummary,
        IReadOnlyList<string> strongAssertions,
        IReadOnlyList<string> toolFindingSummaries,
        IReadOnlyList<string> recentUserTurns,
        IReadOnlyList<string> recentAssistantTurns,
        IReadOnlyList<string> activeAnchors,
        bool identityProfileAllowed,
        DateTimeOffset observedAt)
    {
        return new MemoryObservationRequest(
            sessionId,
            turnId,
            triggerType,
            observedAt,
            new MemoryObservationCurrentTurn(userSummary, assistantSummary, strongAssertions, toolFindingSummaries),
            new MemoryObservationRecentContext(
                MemorySidecarPromptBuilder.BuildSessionSummary(recentUserTurns, recentAssistantTurns),
                recentUserTurns,
                recentAssistantTurns,
                activeAnchors),
            new MemoryObservationPolicyScope(domain, sensitivity, identityProfileAllowed));
    }
}
