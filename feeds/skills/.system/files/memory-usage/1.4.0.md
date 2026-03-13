---
name: memory-usage
description: How SQLite-backed memory now splits automatic durable recall from intentional evidence search
metadata:
  author: netclaw
  version: "1.4.0"
  triggers: conversation start | user shares durable fact | need prior knowledge | memory error | save information
---

## Default Model

Netclaw memory is SQLite-first.

- Automatic recall runs before each user-facing turn.
- Automatic recall injects `durable_fact` only.
- Explicit tools are a deliberate manual-control layer.

Available tools:

- `find_memories`
- `get_memories`
- `store_memory`
- `update_memory`

## Automatic Recall

- Runs before each user-facing turn.
- Uses bounded recall planning plus deterministic gates.
- Injects `durable_fact` only.
- Never injects `evidence` or `trace` into the automatic recall bundle.
- If degraded, continue the turn and treat memory as partial for that turn.

## Intentional Search

Use `find_memories` + `get_memories` when:

- the user explicitly asks what Netclaw remembers
- the automatic recall bundle seems insufficient
- you need targeted retrieval beyond the injected bundle

Normal `find_memories` behavior:

- searches `durable_fact` plus current `evidence`
- excludes `trace`
- hides expired evidence by default

Audit/debug search:

- `find_memories(query, include_stale: true)` may surface expired evidence
- stale evidence is clearly marked with `stale=true`

Two-phase retrieval pattern:

1. `find_memories("query")`
2. `get_memories("id1, id2")`

## Explicit Writes

### `store_memory`

Use only for deliberate remember/save actions:

- explicit remember requests
- intentionally pinning a high-value durable fact, decision, or preference

Do not call `store_memory` reflexively on routine turns.

### `update_memory`

Use only to correct or supersede existing memory.

## What The System Stores

- `durable_fact`: stable facts and preferences
- `evidence`: supporting research, tool findings, and time-bound notes
- `trace`: short-lived execution breadcrumbs

Freshness rules:

- `durable_fact` is non-expiring by default
- `evidence` expires and is excluded from auto recall after expiry
- `trace` is short-lived and never part of normal recall/search behavior

## SOUL Boundary

Do not use identity files as a sink for project facts, research passages, tool
findings, or evidence. `SOUL.md` is only for narrow identity/profile updates.

## Diagnostics

When memory behavior looks wrong:

1. `netclaw status`
2. `netclaw doctor`
3. read `self-diagnostics`
4. read `docs/runbooks/memory-health-and-evals.md`

Useful log events:

- `memory_recall_plan_resolved`
- `memory_recall_plan_fallback`
- `memory_observation_sidecar_completed`
- `memory_observation_gate_result`
- `turn_memory_recall`

## Eval Gate

Before rollout, run the redesigned provider-independent eval suites first, then
optional live smoke checks with local Ollama models.
