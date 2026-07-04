---
name: netclaw-memory
description: "REQUIRED when the user asks what you remember, recall, or know from past conversations, previous sessions, cross-session memory, memory classes, or memory types. Also before using memory tools: find_memories, get_memories, store_memory, update_memory."
metadata:
  author: netclaw
  version: "1.8.0"
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
  `durable_fact` (and occasionally `evidence`) memories into the conversation.
- Recall is **selective by design**: candidates must clear a relevance floor
  and a per-turn character budget, so **many turns inject nothing at all**.
  An absent `[memory-recall]` block means nothing relevant cleared the bar —
  it is not a malfunction. Use `find_memories` when you believe relevant
  memories exist that automatic recall did not surface.
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
- Memory IDs shown by automatic recall, `find_memories`, and `get_memories`
  (e.g. `doc-…` / `rec-…`) are stable, opaque handles. Copy them **verbatim**
  into `get_memories` or `update_memory` — do not rewrite or reformat them.

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

Automatic observation note:
- a non-empty adopted thread window still counts as adopted context for audit
  and approval provenance
- automatic memory suppression only kicks in when the adopted window includes a
  sender other than the current authorized author
- self-only adopted history does not suppress automatic memory formation by
  itself

### `update_memory`

Use only to correct or supersede an existing memory.

Use the memory ID exactly as shown by automatic recall, `find_memories`, or
`get_memories`. For documents, prefer `new_content` when replacing a full
hydrated memory. Use `old_text` + `new_text` only when making a precise
find-and-replace edit. To delete a memory, pass `delete: true`.

## Memory Classes

| Class | Recall | Expiry |
|-------|--------|--------|
| `durable_fact` | Auto-recall when it clears the relevance floor | Never expires |
| `evidence` | Search (`find_memories`); auto-recall only on very strong matches | Expires after 30 days |
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

Identity files (`SOUL.md`, `AGENTS.md`, `TOOLING.md`) define **the agent** —
persona, tone, operating rules, and the foundational user grounding set at init
(name, timezone). Do **not** put project facts, research, tool findings, or
**durable facts and preferences about the user** (favorites, family, history,
working preferences) in identity files — those go through the **memory pipeline**
(`store_memory`) and are recalled when relevant. A user asking you to "remember" a
preference is a memory write, not a `SOUL.md` edit.

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

### Embeddings

Embeddings are provisioned at daemon start when `Memory.Embeddings.Enabled` is
`true` (default `false` for now). When unavailable:
- Log: `memory_embedding_unavailable`
- Daemon status shows: `embeddings: degraded`
- Lexical recall continues to work normally

To repopulate existing memory vectors after enabling embeddings:
```
netclaw memory backfill-embeddings [--force]
```

## Eval Gate

Before rollout, run the redesigned provider-independent eval suites first,
then optional live smoke checks with local Ollama models.
