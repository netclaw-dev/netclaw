## Context

The current SQLite-first memory redesign already establishes the right ownership boundary: sessions enqueue checkpoints, background curation owns persistence, and subagents return findings instead of writing durable memory directly. The remaining problem is upstream quality. Automatic recall still starts from raw natural-language turns, which produces weak lexical queries, and durable memory formation still depends too heavily on deterministic extraction alone, so strong user assertions and high-value tool findings are missed while low-value research passages have nowhere to live except oversized durable facts or `SOUL.md`.

This change adds two narrowly scoped LLM assists inside the existing session and checkpoint architecture:

- `MemoryObservationSidecar`: proposes structured memory candidates from sanitized turn summaries.
- `RecallPlanningSidecar`: proposes a bounded recall plan from the current turn and recent context.

Both assists are advisory only. They do not write SQLite rows, update `SOUL.md`, or execute tools. Netclaw keeps deterministic policy, schema validation, and system-owned writes in the existing checkpoint/store path.

## Goals / Non-Goals

**Goals:**
- Improve memory formation recall by using structured LLM observation instead of only deterministic extraction from raw checkpoints.
- Improve automatic recall quality by using a structured recall planner instead of raw lexical query tokenization.
- Preserve the current ownership rule: LLMs propose, deterministic policy gates decide, system-owned components write.
- Introduce first-class `durable_fact`, `evidence`, and `trace` classes with clear recall and expiry behavior.
- Keep automatic recall distinct from intentional memory search.
- Reuse existing Netclaw infrastructure wherever possible, especially the current sidecar model and checkpoint/store flow.
- Redesign evals so formation, recall, evidence separation, and policy-gate correctness are measurable.

**Non-Goals:**
- No direct durable writes from a sidecar or subagent.
- No per-turn autonomous tool loop for memory observation or recall planning.
- No replacement of the existing explicit `find_memories` / `get_memories` / `store_memory` / `update_memory` tool surface.
- No broadening of `SOUL.md` into a general-purpose knowledge store.
- No vector store or embedding dependency in this change.

## Decisions

### Decision: Reuse the existing session sidecar pattern first, not `SubAgentActor`

Implementation should start by generalizing the existing title-generation sidecar pattern in `LlmSessionActor` into a reusable structured sidecar runner, for example `SessionSidecarRunner` or `ISessionSidecarInvoker`.

Why sidecar first:
- These tasks are one-shot, bounded, no-tool JSON generations, just like title generation and observer summaries.
- They already fit the existing `Compaction`/sidecar model role and `SessionConfig.SidecarLlmTimeoutSeconds` budget.
- They run on hot paths (`before turn` and `after turn/checkpoint`), so actor-local lightweight calls are preferable to spawning a `SubAgentActor` with tool-loop machinery.
- A sidecar is easier to make fail-closed: invalid JSON, timeout, or empty output simply degrades to deterministic fallback.

Why not subagent first:
- `SubAgentActor` is designed for autonomous tool loops and structured findings after multiple steps.
- Recall planning and memory observation should not need tools or independent durable-memory policy.
- Spawning a subagent on every turn adds more latency, state transitions, and observability noise than the problem requires.

When subagents may still be useful later:
- Deep intentional search that must search tools, summarize many evidence items, or reconcile ambiguous anchors.
- Offline batch curation or migration work that truly benefits from a multi-step tool loop.

Initial recommendation: build the sidecar path first, keep the contract shape subagent-compatible, and only introduce an internal platform-owned subagent later if evidence synthesis grows beyond one-shot JSON planning.

### Decision: Add three memory classes orthogonal to document/record shape

The SQLite substrate keeps the existing durable shapes (`documents`, `records`, `edges`), but each stored item also carries a `memory_class`:

- `durable_fact`: stable preferences, identity facts, project facts, operator assertions, durable conclusions.
- `evidence`: supporting passages from search results, tool output snippets, one-off research notes, citations, and time-bound observations.
- `trace`: short-lived execution breadcrumbs and turn-local diagnostic artifacts.

Recommended storage semantics:
- `durable_fact` may land as a `document` or `record` depending on update semantics.
- `evidence` lands as immutable `record` rows with provenance and expiry.
- `trace` lands as immutable `record` rows with required expiry and `recallMode=never` by default.

Suggested schema extensions:
- Add `memory_class TEXT NOT NULL` to `documents` and `records`.
- Add `expires_at_ms INTEGER NULL` to `documents` and `records`.
- Add provenance fields for evidence/trace records, such as `source_kind`, `source_ref`, and `supporting_excerpt` in payload JSON.

Why this split:
- It separates durable knowledge from searchable support material.
- It lets automatic recall stay clean without hiding evidence from intentional search.
- It gives `trace` a place to exist without polluting durable recall.

### Decision: `MemoryObservationSidecar` produces proposals, not writes

`MemoryObservationSidecar` runs after eligible turn/checkpoint events on a sanitized summary payload and returns `MemoryProposal[]`. The sidecar is advisory only.

The session builds a sanitized observation request from:
- normalized user assertions from the current turn
- recent assistant commitments and summaries
- accepted tool findings summaries
- accepted subagent findings summaries
- active project/domain context

The sidecar must not receive raw full transcripts, secrets, or unrestricted tool payloads. It sees only sanitized summaries and bounded excerpts.

Accepted proposal operation enum:
- `upsert_document`
- `append_record`
- `supersede_record`
- `expire_record`
- `ignore`

Those are still proposals. The deterministic gate can reject, downgrade, or remap them before any checkpoint or write occurs.

### Decision: `RecallPlanningSidecar` plans recall, while deterministic code executes it

`RecallPlanningSidecar` runs before the user-facing model call. It converts the current turn and recent context into a bounded `RecallQueryPlan`.

The plan includes:
- `mode`: `automatic` or `intentional`
- normalized intent and anchor hints
- query terms and filters
- allowed memory classes
- result count/token clamps
- optional freshness requirements

The planner does not query SQLite directly. Deterministic code clamps and executes the plan against the repository.

Recall-path rules:
- Automatic recall: `durable_fact` only, bounded, low-latency, prompt injection path.
- Intentional search: `durable_fact` plus `evidence`, explicit tool path, no automatic prompt injection of evidence.

This keeps auto recall and intentional search distinct even though they can share the same planner contract and repository.

### Decision: Deterministic policy gates sit between proposals/plans and execution

There are two gate layers.

1. `MemoryProposalGate`
   - Validates JSON schema and required fields.
   - Rejects unknown operations or classes.
   - Resolves/normalizes policy envelope (`domain`, `sensitivity`, `recallMode`, `confidence`).
   - Enforces source-to-class rules.
   - Applies SOUL boundary rules.
   - Derives or validates expiry.
   - Deduplicates against recent memory.
   - Converts accepted proposals into checkpoint payload operations.

2. `RecallPlanGate`
   - Validates plan schema.
   - Forces `automatic` mode to `memoryClasses=["durable_fact"]` regardless of sidecar suggestion.
   - Allows `intentional` mode to include `evidence` but not `trace` unless operator/debug path explicitly enables it.
   - Clamps `maxResults`, token budget, and latency budget.
   - Filters denied domains/sensitivity before repository execution.

The sidecar cannot bypass these gates. A valid-looking but policy-invalid proposal still dies in deterministic code.

### Decision: Route accepted observation proposals through existing checkpoint/store infrastructure

Netclaw should reuse the current checkpoint pipeline instead of inventing a second write path.

Write flow:
- Session-side `MemoryObservationSidecar` returns proposals.
- `MemoryProposalGate` accepts/rejects them.
- Accepted proposals become a new checkpoint trigger, for example `observed-memory-proposals`.
- `IMemoryCheckpointSink.EnqueueAsync(...)` persists the checkpoint.
- `MemoryCurationWorkerService` picks it up.
- `MemoryCurationPipeline` revalidates accepted operations, resolves anchors/documents/records, and commits a SQLite transaction.

This preserves the current durability, retry, and audit behavior. It also means explicit `store_memory` and sidecar-observed writes converge on the same persistence path.

### Decision: `SOUL.md` remains a narrow identity/profile surface

Observation sidecars and recall planning must never treat `SOUL.md` as a general memory sink.

Allowed `SOUL.md` updates:
- agent name
- tone/persona
- standing communication preferences
- operator relationship/serving style
- explicit identity/profile changes confirmed through the existing self-configuration path

Not allowed in `SOUL.md`:
- project facts
- tool findings
- research passages
- environment state
- durable evidence
- trace or checkpoint artifacts

If the sidecar thinks something looks identity-related, it may emit a proposal with `targetSurface="identity_profile"`, but deterministic gating must still require an explicit identity-safe category and route it through the existing identity-file update flow rather than memory auto-write.

### Decision: Evidence and trace require freshness semantics

Expiry rules:
- `durable_fact`: no expiry by default; may still use freshness for ranking.
- `evidence`: expiry required. If missing, the gate derives a default based on source:
  - search/web passage: 14 days
  - tool-result excerpt or one-off research note: 30 days
- `trace`: expiry required and short. Default 72 hours.

Recall behavior:
- expired `evidence` and `trace` are excluded from automatic recall
- expired `evidence` may still appear in intentional search only if explicitly requested for audit/debug and clearly marked stale
- `trace` is never part of normal intentional search unless operator/debug mode requests it

### Decision: Redesign evals around formation, recall, and separation

The existing seeded-memory evals are not enough because they skip the formation path. This change adds end-to-end evals where the system must first observe and store, then later recall or search.

Required suites:
- `formation_then_auto_recall`
- `formation_then_intentional_search`
- `evidence_vs_durable_separation`
- `proposal_gate_rejection`
- `soul_boundary`
- `expiry_and_staleness`

Primary thresholds:

Smoke suite:
- proposal schema validity: 1.00
- deterministic gate correctness: 1.00
- durable-fact formation precision: >= 0.90
- automatic durable-fact recall hit rate: >= 0.90
- automatic evidence leakage: 0.00
- intentional-search evidence hit rate: >= 0.90
- explicit write truthfulness: 1.00

Realistic sanitized suite:
- proposal schema validity: >= 0.98
- durable-fact formation precision: >= 0.80
- automatic durable-fact recall hit rate: >= 0.75
- automatic evidence leakage: <= 0.02
- intentional-search evidence hit rate: >= 0.80
- explicit write truthfulness: 1.00

Stability gates:
- smoke thresholds must pass in 5 consecutive CI runs
- realistic thresholds must pass in 3 consecutive local-Ollama gate runs before rollout default enablement

## Architecture And Message Flow

### Automatic recall flow

```text
Slack/CLI turn
  -> LlmSessionActor
  -> Build RecallPlanningRequest (sanitized current turn + recent context)
  -> RecallPlanningSidecar (one-shot JSON, no tools)
  -> RecallPlanGate (schema/policy/clamps)
  -> MemoryRepository query execution
  -> Final policy filter + token clamp
  -> Inject automatic recall bundle (durable_fact only)
  -> Main model turn
```

### Observation and durable write flow

```text
Turn completed / tool finding accepted / subagent finding accepted
  -> LlmSessionActor or CheckpointDetector
  -> Build MemoryObservationRequest (sanitized summary)
  -> MemoryObservationSidecar (one-shot JSON, no tools)
  -> MemoryProposalGate (schema/class/policy/expiry/dedupe)
  -> IMemoryCheckpointSink.EnqueueAsync(trigger=observed-memory-proposals)
  -> SQLite memory_checkpoints row persisted
  -> MemoryCurationWorkerService
  -> MemoryCurationPipeline revalidation + anchor resolution
  -> SQLite transaction writes documents/records/edges
```

### Intentional search flow

```text
Frontline model calls find_memories
  -> Build RecallPlanningRequest(mode=intentional)
  -> RecallPlanningSidecar
  -> RecallPlanGate forces allowed classes = durable_fact + evidence
  -> MemoryRepository search
  -> Lightweight results returned to model
  -> Model may call get_memories for hydration
```

## Data Contracts / JSON Schemas

### `MemoryObservationRequest`

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "MemoryObservationRequest",
  "type": "object",
  "required": [
    "sessionId",
    "turnId",
    "triggerType",
    "observedAt",
    "currentTurn",
    "recentContext",
    "policyScope"
  ],
  "properties": {
    "sessionId": { "type": "string" },
    "turnId": { "type": "string" },
    "triggerType": {
      "type": "string",
      "enum": [
        "turn_completed",
        "explicit_save",
        "verified_tool_finding",
        "accepted_subagent_finding",
        "compaction_boundary"
      ]
    },
    "observedAt": { "type": "string", "format": "date-time" },
    "currentTurn": {
      "type": "object",
      "required": ["userSummary", "assistantSummary"],
      "properties": {
        "userSummary": { "type": "string", "maxLength": 4000 },
        "assistantSummary": { "type": "string", "maxLength": 4000 },
        "strongAssertions": {
          "type": "array",
          "items": { "type": "string", "maxLength": 500 }
        },
        "toolFindingSummaries": {
          "type": "array",
          "items": { "type": "string", "maxLength": 1000 }
        }
      }
    },
    "recentContext": {
      "type": "object",
      "required": ["sessionSummary"],
      "properties": {
        "sessionSummary": { "type": "string", "maxLength": 4000 },
        "activeAnchors": {
          "type": "array",
          "items": { "type": "string", "maxLength": 200 }
        }
      }
    },
    "policyScope": {
      "type": "object",
      "required": ["allowedDomains", "defaultSensitivity"],
      "properties": {
        "allowedDomains": { "type": "array", "items": { "type": "string" } },
        "defaultSensitivity": { "type": "string" },
        "allowIdentityProfileHints": { "type": "boolean" }
      }
    }
  }
}
```

### `MemoryProposal`

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "MemoryProposal",
  "type": "object",
  "required": [
    "proposalId",
    "memoryClass",
    "operation",
    "targetSurface",
    "summary",
    "confidence"
  ],
  "properties": {
    "proposalId": { "type": "string" },
    "memoryClass": {
      "type": "string",
      "enum": ["durable_fact", "evidence", "trace"]
    },
    "operation": {
      "type": "string",
      "enum": [
        "upsert_document",
        "append_record",
        "supersede_record",
        "expire_record",
        "ignore"
      ]
    },
    "targetSurface": {
      "type": "string",
      "enum": ["sqlite_memory", "identity_profile"]
    },
    "anchorHints": {
      "type": "array",
      "items": { "type": "string", "maxLength": 200 }
    },
    "title": { "type": ["string", "null"], "maxLength": 200 },
    "summary": { "type": "string", "maxLength": 4000 },
    "supportingExcerpt": { "type": ["string", "null"], "maxLength": 2000 },
    "domain": { "type": ["string", "null"] },
    "sensitivity": { "type": ["string", "null"] },
    "recallMode": { "type": ["string", "null"] },
    "observedAt": { "type": ["string", "null"], "format": "date-time" },
    "expiresAt": { "type": ["string", "null"], "format": "date-time" },
    "sourceKind": {
      "type": ["string", "null"],
      "enum": [null, "user_assertion", "assistant_commitment", "tool_result", "web_passage", "subagent_finding", "trace"]
    },
    "sourceRef": { "type": ["string", "null"], "maxLength": 500 },
    "confidence": { "type": "number", "minimum": 0, "maximum": 1 }
  }
}
```

### `RecallPlanningRequest`

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "RecallPlanningRequest",
  "type": "object",
  "required": [
    "sessionId",
    "turnId",
    "mode",
    "userTurn",
    "recentContext",
    "policyScope",
    "budget"
  ],
  "properties": {
    "sessionId": { "type": "string" },
    "turnId": { "type": "string" },
    "mode": { "type": "string", "enum": ["automatic", "intentional"] },
    "userTurn": {
      "type": "object",
      "required": ["text"],
      "properties": {
        "text": { "type": "string", "maxLength": 4000 },
        "explicitMemoryIntent": { "type": ["string", "null"] }
      }
    },
    "recentContext": {
      "type": "object",
      "required": ["sessionSummary"],
      "properties": {
        "sessionSummary": { "type": "string", "maxLength": 4000 },
        "activeAnchors": {
          "type": "array",
          "items": { "type": "string", "maxLength": 200 }
        }
      }
    },
    "policyScope": {
      "type": "object",
      "required": ["allowedDomains"],
      "properties": {
        "allowedDomains": { "type": "array", "items": { "type": "string" } },
        "blockedSensitivity": { "type": "array", "items": { "type": "string" } }
      }
    },
    "budget": {
      "type": "object",
      "required": ["maxResults", "maxTokens", "latencyBudgetMs"],
      "properties": {
        "maxResults": { "type": "integer", "minimum": 1, "maximum": 20 },
        "maxTokens": { "type": "integer", "minimum": 128, "maximum": 4000 },
        "latencyBudgetMs": { "type": "integer", "minimum": 50, "maximum": 5000 }
      }
    }
  }
}
```

### `RecallQueryPlan`

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "RecallQueryPlan",
  "type": "object",
  "required": [
    "mode",
    "queryTerms",
    "memoryClasses",
    "anchorHints",
    "maxResults",
    "maxTokens"
  ],
  "properties": {
    "mode": { "type": "string", "enum": ["automatic", "intentional"] },
    "intent": { "type": ["string", "null"] },
    "queryTerms": {
      "type": "array",
      "items": { "type": "string", "maxLength": 100 }
    },
    "anchorHints": {
      "type": "array",
      "items": { "type": "string", "maxLength": 200 }
    },
    "memoryClasses": {
      "type": "array",
      "items": {
        "type": "string",
        "enum": ["durable_fact", "evidence", "trace"]
      }
    },
    "freshness": {
      "type": ["object", "null"],
      "properties": {
        "requireUnexpired": { "type": "boolean" },
        "preferNewerThanDays": { "type": ["integer", "null"], "minimum": 1 }
      }
    },
    "maxResults": { "type": "integer", "minimum": 1, "maximum": 20 },
    "maxTokens": { "type": "integer", "minimum": 128, "maximum": 4000 },
    "reason": { "type": ["string", "null"], "maxLength": 500 }
  }
}
```

## Failure Modes And Recovery Behavior

- Sidecar timeout: fall back to deterministic baseline behavior. Automatic recall uses current lexical fallback over durable facts only; observation falls back to existing rules-only extraction.
- Invalid JSON/schema mismatch: reject output, log structured sidecar failure, increment eval/diagnostic counters, and continue in degraded mode.
- Gate rejection: do not enqueue checkpoint/query with rejected content; record reason for audit.
- SQLite/query failure after plan acceptance: automatic recall degrades without blocking the turn; intentional search returns a controlled degraded result.
- Worker failure after accepted proposals are checkpointed: existing retry behavior applies; no partial write is acknowledged as complete.

## Risks / Trade-offs

- [Risk] Hot-path sidecar latency may slow turns. -> Mitigation: use sidecar model role, strict JSON schemas, and hard latency clamps with deterministic fallback.
- [Risk] Sidecar proposals may over-class evidence as durable facts. -> Mitigation: deterministic class/source rules, dedupe checks, and formation precision evals.
- [Risk] Evidence expiry defaults may be too aggressive or too lax. -> Mitigation: centralize defaults in config and gate changes through the expiry suite.
- [Risk] Reusing sidecars may delay richer multi-step memory workflows. -> Mitigation: keep contract shapes compatible with a future internal subagent implementation.
- [Risk] Narrow `SOUL.md` rules may frustrate attempts to store preferences as identity. -> Mitigation: make the boundary explicit in prompt guidance and eval the `soul_boundary` suite.

## Migration Plan

1. Extract the current title-generation pattern into a reusable structured sidecar runner that supports typed JSON responses and existing timeout/model settings.
2. Extend SQLite schema for `memory_class` and `expires_at_ms`, plus any needed provenance payload fields.
3. Add `MemoryObservationRequest` / `MemoryProposal` contracts, `MemoryProposalGate`, and a new checkpoint trigger for accepted observed proposals.
4. Add `RecallPlanningRequest` / `RecallQueryPlan` contracts, `RecallPlanGate`, and repository execution clamps.
5. Update `find_memories` and `get_memories` to use intentional-search planning and to include `evidence` results while keeping automatic recall `durable_fact` only.
6. Add `SOUL.md` boundary enforcement and identity-profile routing rules.
7. Ship the new eval suites and stability gates; keep rollout behind a feature flag until thresholds pass.
8. Roll forward by enabling the sidecar-assisted paths per environment; roll back by disabling the feature flag and using the current deterministic-only observation plus lexical recall fallback.

## Open Questions

- Should automatic recall call the planner on every turn, or skip planner invocation for obviously low-memory-intent turns and use a cheaper deterministic fast path?
- Should `trace` be stored in the same `records` table with `memory_class=trace`, or in a dedicated trace table that still participates in expiry cleanup?
- Should intentional search expose stale evidence by default with a stale marker, or require an explicit `include_stale` option in the tool args?
