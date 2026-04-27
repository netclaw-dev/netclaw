---
name: netclaw-memory
description: "REQUIRED when the user asks what you remember, recall, or know from past conversations, previous sessions, or cross-session memory. Also before using memory tools: find_memories, get_memories, store_memory, update_memory."
metadata:
  author: netclaw
  version: "1.4.0"
---

# Netclaw Memory

Read this before using any memory tool. It defines how memory works and
when to use each tool.

## Audience and Feature Gating

Memory is subject to two independent gates:

- **Audience gate:** Public sessions have no access to memory tools, automatic
  recall, or memory extraction. Memory is fully inert for Public — no reads,
  writes, or recall. Historical memories authored by Public sessions are also
  excluded from recall and search for all audiences.
- **Deployment gate:** `Memory.Enabled` in `netclaw.json` (default `true`).
  When `false`, memory is disabled for ALL audiences — recall returns empty,
  memory tools are hidden from discovery, and the observation sidecar skips
  extraction.

Both gates must pass for memory to function.

## How Memory Works

- **Automatic recall** runs before each user turn and injects relevant
  `durable_fact` memories into the conversation.
- Recall is **policy-aware**: `audience` and `boundary` still govern what
  can be surfaced for the current turn.
- Recall resolves once at turn start and the same bundle is reused during
  tool-loop follow-ups.
- Recalled memories may persist into session history for ongoing context, so
  per-turn policy is **first-contact gating**, not a way to retroactively
  scrub information already surfaced earlier in the session.
- **Explicit tools** are a manual-control layer on top of automatic recall.
- Memory is SQLite-backed and cross-session only within the active
  domain/boundary policy envelope.

## When to Use Explicit Tools

### `find_memories` + `get_memories`

Use when:
- The user explicitly asks what you remember
- Automatic recall seems insufficient for the question
- You need targeted retrieval beyond the injected bundle

Pattern: `find_memories("query")` -> scan results -> `get_memories("id1,id2")`

Normal `find_memories` behavior:
- searches `durable_fact` plus current `evidence`
- excludes `trace`
- hides expired evidence by default
- respects the current turn's effective `audience` and `boundary`

### `store_memory`

Use only for deliberate save requests:
- User explicitly says "remember this" or "save this for later"
- Pinning a high-value fact, decision, or preference

Do NOT call `store_memory` reflexively on routine turns - the observation
sidecar handles background memory formation automatically.

Policy rules for explicit writes:
- explicit writes still inherit the current turn's `audience` and `boundary`
- explicit writes may narrow policy scope, but must never widen it
- raw secrets, credentials, tokens, and private keys are never durable memory

### `update_memory`

Use only to correct or supersede an existing memory.

## Memory Classes

| Class | Recall | Expiry |
|-------|--------|--------|
| `durable_fact` | Auto-recalled each turn | Never expires |
| `evidence` | Search only (`find_memories`) | Expires after 30 days |
| `trace` | Not searchable | Expires after 72 hours |

## Policy Envelope

Every durable memory item should be understood as carrying:

- `memory_class`
- `audience`
- `boundary`
- `domain`
- `sensitivity`
- `recall_mode`

Write-time and read-time policy both matter. Correct classification alone is
not enough - recall and intentional search must also honor the active trust
context.

## Identity vs Memory

Do not put project facts, research, or tool findings in identity files.
`SOUL.md` is only for narrow identity/profile updates. Everything else
goes through the memory pipeline.

If unsure, load `netclaw-operations` for the identity-vs-memory triage guide.

## Diagnostics

When memory behavior looks wrong:

1. `netclaw status`
2. `netclaw doctor`
3. load `netclaw-operations`
4. read `docs/runbooks/memory-health-and-evals.md`

Useful log events:

**Recall pipeline** (grep for `memory_retrieval`):
- `memory_retrieval_request_plan` — query tokenization, facets, soft scopes, anchor hints
- `memory_retrieval_candidate_selection` — all candidates with selector scores
- `memory_retrieval_final` — floor filtering results, final injected items
- `turn_memory_recall` — summary event with item count and duration

**Formation pipeline** (grep for `memory_observation`):
- `memory_observation_sidecar_completed`
- `memory_observation_gate_result`

## Eval Gate

Before rollout, run the redesigned provider-independent eval suites first,
then optional live smoke checks with local Ollama models.
