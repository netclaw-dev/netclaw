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

            Example evidence (agent-derived finding):
            {
              "operation": "append_record",
              "memoryClass": "evidence",
              "subjectKind": "project",
              "subjectValue": "netclaw",
              "anchor": { "canonicalName": "pr-394-review", "anchorType": "review" },
              "title": "PR #394 Review: Skill Platform Hardening",
              "content": "PR #394 adds skill management CRUD tooling and a five-tier trust system (System > User > Community > External > Agent). Key findings: content security scanning interface, atomic file writes, system directory write protection.",
              "aliases": ["PR 394", "skill trust tiers", "skill management"],
              "facets": ["code_review", "project_artifact"],
              "slots": [],
              "relations": [],
              "recallMode": "searchable",
              "sensitivity": "normal",
              "confidence": 0.80,
              "freshUntilMs": null,
              "expiresAtMs": null,
              "targetSurface": null,
              "rationale": "Agent-derived findings from PR review — synthesized conclusions, not raw diff output."
            }

            Rules:
            - Strong stable user assertions and durable working preferences become durable_fact.
            - Conclusions, learnings, and discoveries that the agent arrived at through tool use, analysis, or research become evidence with moderate confidence (0.7-0.85). Examples: PR review findings, research comparisons, discovered constraints or errors, task outcomes.
            - Raw search results, hotel/flight options, price lists, and transient research data become evidence only when the agent has drawn a conclusion from them. Do not store raw tool output.
            - Routine tool invocation logs, raw API responses, raw search result listings, status checks, and execution breadcrumbs become trace or ignore. The key distinction: synthesized knowledge → evidence; raw output → trace.
            - Never write secrets as auto-recall memories.
            - Never use SOUL.md as a sink for project facts, research passages, or evidence.
            - When in doubt between evidence and ignore, prefer evidence with moderate confidence (0.7-0.8) rather than suppressing the observation.
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
