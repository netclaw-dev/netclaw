using System.Text.Json;
using System.Text;

namespace Netclaw.Actors.Sessions;

public static class MemorySidecarPromptBuilder
{
    public static string BuildMemoryObservationSystemPrompt()
    {
        return """
            You are a memory observation sidecar.
            Return JSON only.

            Your job is to propose memory items from a sanitized turn summary.
            You may propose only these memory classes:
            - durable_fact
            - evidence
            - trace

            You may propose only these operations:
            - upsert_document
            - append_record
            - ignore

            Rules:
            - Strong stable user assertions and durable working preferences become durable_fact.
            - Search results, hotel/flight options, passages, prices, and transient research become evidence.
            - Diagnostic chatter and execution breadcrumbs become trace or ignore.
            - Never write secrets as auto-recall memories.
            - Never use SOUL.md as a sink for project facts, research passages, or evidence.
            - Be conservative.
            """;
    }

    public static string BuildMemoryObservationUserPrompt(MemoryObservationRequest request)
    {
        return JsonSerializer.Serialize(request);
    }

    public static string BuildRecallPlanningSystemPrompt()
    {
        return """
            You are a recall planning sidecar.
            Return JSON only.

            Build a compact retrieval plan from a user query and recent context.

            Rules:
            - Prefer meaningful entities, nouns, airports, venues, product names, and constraints.
            - Strip conversational filler and weak stopword-style terms.
            - For automatic mode, plan only durable_fact retrieval.
            - For intentional mode, durable_fact and evidence may be searched.
            - Do not answer the user; only produce a retrieval plan.
            """;
    }

    public static string BuildRecallPlanningUserPrompt(RecallPlanningRequest request)
    {
        return JsonSerializer.Serialize(request);
    }

    public static string BuildSessionSummary(IReadOnlyList<string> recentUserTurns, IReadOnlyList<string> recentAssistantTurns)
    {
        var sb = new StringBuilder();
        foreach (var text in recentUserTurns.TakeLast(3))
            sb.AppendLine($"User: {text}");
        foreach (var text in recentAssistantTurns.TakeLast(3))
            sb.AppendLine($"Assistant: {text}");
        return sb.ToString().TrimEnd();
    }
}
