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

            Return this exact top-level shape:
            { "proposals": [ ... ] }

            Your job is to propose memory items from a sanitized turn summary.
            You may propose only these memory classes:
            - durable_fact
            - evidence
            - trace

            You may propose only these operations:
            - upsert_document
            - append_record
            - ignore

            Do not invent synonyms. Do not use any other operation or memory class value.
            If no memory should be created, return { "proposals": [] }.

            For durable_fact or evidence proposals, include:
            - anchor { canonicalName, anchorType }
            - aliases (non-empty array)
            - facets (non-empty array)

            Use slots only when clearly appropriate, such as:
            - origin_airport
            - preferred_airline
            - trip_plan
            - venue_area

            Example durable_fact:
            {
              "operation": "upsert_document",
              "memoryClass": "durable_fact",
              "subjectKind": "user",
              "subjectValue": "self",
              "anchor": { "canonicalName": "user-travel-airline", "anchorType": "preference" },
              "title": "Travel Profile: Preferred Airline",
              "content": "Preferred airline is United Airlines because status benefits matter.",
              "aliases": ["preferred airline", "United Airlines", "status with United"],
              "facets": ["travel_profile", "user_preference"],
              "slots": ["preferred_airline"],
              "relations": [],
              "recallMode": "auto",
              "sensitivity": "normal",
              "confidence": 0.96,
              "freshUntilMs": null,
              "expiresAtMs": null,
              "targetSurface": null,
              "rationale": "Stable user preference stated explicitly."
            }

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
