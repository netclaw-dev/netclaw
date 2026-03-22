---
name: netclaw-memory
description: "REQUIRED before using find_memories, get_memories, store_memory, or update_memory. Read this first when the user asks what you remember, wants something saved, or asks about past conversations."
metadata:
  author: netclaw
  version: "1.0.0"
---

# Netclaw Memory

Read this before using any memory tool. It defines how memory works and
when to use each tool.

## How Memory Works

- **Automatic recall** runs before each user turn — injects relevant
  `durable_fact` memories into the conversation automatically.
- **Explicit tools** are a manual-control layer on top of automatic recall.
- Memory is SQLite-backed and cross-session within the same domain.

## When to Use Explicit Tools

### `find_memories` + `get_memories`

Use when:
- The user explicitly asks what you remember
- Automatic recall seems insufficient for the question
- You need targeted retrieval beyond the injected bundle

Pattern: `find_memories("query")` → scan results → `get_memories("id1,id2")`

### `store_memory`

Use only for deliberate save requests:
- User explicitly says "remember this" or "save this for later"
- Pinning a high-value fact, decision, or preference

Do NOT call `store_memory` reflexively on routine turns — the observation
sidecar handles background memory formation automatically.

### `update_memory`

Use only to correct or supersede an existing memory.

## Memory Classes

| Class | Recall | Expiry |
|-------|--------|--------|
| `durable_fact` | Auto-recalled each turn | Never expires |
| `evidence` | Search only (`find_memories`) | Expires after 30 days |
| `trace` | Not searchable | Expires after 72 hours |

## Identity vs Memory

Do not put project facts, research, or tool findings in identity files.
`SOUL.md` is only for narrow identity/profile updates. Everything else
goes through the memory pipeline.

If unsure, load `netclaw-operations` for the identity vs memory triage guide.
