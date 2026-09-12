// -----------------------------------------------------------------------
// <copyright file="SidecarRecallPlanner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

public sealed class SidecarRecallPlanner
{
    public RecallPlanningRequest BuildRequest(
        SessionId sessionId,
        string userText,
        IReadOnlyList<string> recentUserTurns,
        IReadOnlyList<string> recentAssistantTurns,
        IReadOnlyList<string> recentEntities,
        string mode,
        int maxQueryTerms,
        int maxResults)
    {
        return new RecallPlanningRequest(
            sessionId.Value,
            mode,
            userText,
            recentUserTurns,
            recentAssistantTurns,
            recentEntities,
            maxQueryTerms,
            maxResults);
    }
}
