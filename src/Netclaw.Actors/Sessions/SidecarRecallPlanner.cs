namespace Netclaw.Actors.Sessions;

public sealed class SidecarRecallPlanner
{
    public RecallPlanningRequest BuildRequest(
        string sessionId,
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
            mode,
            userText,
            recentUserTurns,
            recentAssistantTurns,
            recentEntities,
            maxQueryTerms,
            maxResults);
    }
}
