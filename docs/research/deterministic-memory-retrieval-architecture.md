# Deterministic Memory Retrieval Architecture

Date: 2026-03-11
Status: Proposed architecture derived from retrieval PoCs

## Purpose

This document proposes a production architecture for Netclaw memory retrieval
based on the deterministic proof-of-concept work in
`src/Netclaw.MemoryRetrievalPoC.Tests/`.

The goal is to move automatic recall away from an LLM planner in the hot path
and toward a layered, deterministic system that is:

- fast
- explainable
- bounded by runtime-owned scope
- compatible with SQLite persistence
- capable of both ranked retrieval and bundle retrieval

This is a design note, not an implementation spec.

## Problem Summary

The current sidecar-planned recall path is brittle in production-like runs:

- planner timeouts degrade recall
- JSON-shape errors break observation/planning
- weak fallback search can return zero useful items
- flat top-N recall is not expressive enough for composite prompts

The PoCs show that we can replace much of this with a deterministic pipeline.

## Key Design Principles

1. **Hard scope is system-owned**
   - Slack workspace, channel, DM participant, thread, and configuration define
      the legal search boundary.
2. **Soft scope is conversation-owned**
   - Thread title, active topic, prompt entities, speaker profile, and recent
     anchors define what should be searched first.
3. **Write time carries semantic cost**
   - Extract aliases, facets, anchors, and relations once when memory is formed.
4. **Read time stays deterministic**
   - Candidate filtering, reranking, and bundle assembly happen without an LLM.
5. **Ranked and bundle retrieval both exist**
   - Simple prompts use ranked hits.
   - Composite prompts use bundle slots.

## Retrieval Tiers

The proposed production pipeline has four tiers.

```text
Incoming message
    |
    v
[Tier 0] Runtime scope resolution
    |
    v
[Tier 1] Deterministic request planning
    |
    v
[Tier 2] Cheap candidate selection in SQLite
    |
    v
[Tier 3] Deterministic reranking / bundle assembly
    |
    v
Injected recall set
```

### Tier 0 - Runtime Scope Resolution

This layer resolves the hard boundary before any search happens.

Inputs:

- Slack workspace
- Slack channel
- Slack thread
- DM participant
- project/channel registration

Outputs:

- hard scope domain
- allowed memory classes
- sensitivity policy
- expiry behavior

Examples:

- alert channel -> `project:signalr`
- DM with Aaron -> `user:aaron`
- TextForge channel -> `project:textforge`

Hard scope is not inferred by the LLM.

## Tier 1 - Deterministic Request Planning

This layer takes runtime context plus prompt text and builds a structured
retrieval request.

### Inputs

- hard scope from Tier 0
- prompt text
- optional thread title
- optional recent topic state

### Outputs

- hard scope
- soft scopes
- retrieval mode
- lexical terms
- inferred facets
- anchor hints
- candidate limit
- allowed memory classes
- excluded sensitivity
- expiry policy

### Example Type

```csharp
internal sealed record RetrievalRequestPlan(
    string HardScope,
    IReadOnlyList<string> SoftScopes,
    string RetrievalMode,
    IReadOnlyList<string> LexicalTerms,
    IReadOnlyList<string> Facets,
    IReadOnlyList<string> AnchorHints,
    int CandidateLimit,
    IReadOnlyList<string> AllowedMemoryClasses,
    IReadOnlyList<string> ExcludedSensitivity,
    bool ExcludeExpired);
```

### Role

This layer answers:

- what memory universe is legal?
- what topic/project should narrow search?
- should we look for ranked hits or bundle slots?

### Primary Relevance Signals

Not all soft-scope signals are equally strong.

Recommended ordering:

1. explicit named entities or proper nouns in the prompt
2. speaker-specific profile and stable preferences
3. current thread/topic/title
4. recent active anchors in the session
5. channel/workspace priors

This means Slack metadata is often more useful as a permission/container
boundary than as the main relevance signal.

### Practical Behavior

- DM query about `TextForge`:
  - hard scope: `user:aaron`
  - soft scope: `project:textforge`
  - mode: `ranked`
- DM travel planning query:
  - hard scope: `user:aaron`
  - soft scope: `Stir Trek 2026 travel planning`, `scope:travel`
  - mode: `bundle`
- alert-channel incident query:
  - hard scope: `project:signalr`
  - soft scope: `scope:ops`, `worker-b alerts`
  - mode: `ranked`

### Generic Prompt Behavior

Not every prompt deserves meaningful memory retrieval.

Examples:

- `what's the best way to find cheap flights`
  - generic advice query
  - low memory activation
  - probably better served by world knowledge or live search
- `what's the cheapest flight for me to Boston`
  - named entity + first-person context
  - medium to high memory activation
  - likely relevant: origin airport, preferred airline

The presence of proper nouns or named entities should be treated as a strong
activation signal, but not the only one.

## Tier 2 - Cheap Candidate Selection In SQLite

This is the narrowing layer. It should be fast and predictable.

### Inputs

- `RetrievalRequestPlan`
- documents in the hard scope

### Filters

- domain / hard scope
- memory class
- recall mode
- sensitivity
- expiry/freshness

### Signals

- lexical terms
- markers
- anchor hints
- canonical names
- aliases
- stored facets
- soft-scope/topic hints

### Output

- 20-100 candidates, not the whole database

### Why this matters

The reranker should not inspect the full SQLite corpus every turn. Candidate
selection should reduce the problem size first.

```text
SQLite memory store
    |
    |-- filter by domain / recall / sensitivity / expiry
    |-- score by lexical / marker / alias / facet / anchor hints
    v
candidate set
```

### Candidate Selection Contract

The candidate selector should be deterministic and cheap.

It is acceptable for this layer to be imperfect, because Tier 3 will rerank.

## Tier 3 - Deterministic Reranking And Bundle Assembly

This is where the PoC retrieval engine fits.

### Inputs

- candidate documents
- candidate edges / relations
- query features

### Ranking Signals

- marker matches
- lexical matches
- title/body/anchor weighting
- bigrams
- confidence
- inferred facets
- inferred neighborhood propagation
- intent-sensitive weighting

### Output Modes

#### Ranked Mode

Use for direct prompts.

Examples:

- `What airline do I usually take?`
- `Summarize BETA_INCIDENT_002`

Output:

- one or a few best documents

#### Bundle Mode

Use for composite prompts.

Examples:

- `What airport and airline do I usually use?`
- `What's the best flight / hotel combination for me?`

Output slots:

- `origin_airport`
- `preferred_airline`
- `trip_plan`
- `venue_area`

### Why bundle mode exists

Some queries are not “find the best document.”
They are “assemble the right answer ingredients.”

## Scope Layering In Practice

### Shared Channels

Shared channels are often a good hard boundary.

```text
Slack channel #signalr-alerts
    -> hard scope: project:signalr
    -> soft scope from prompt/thread: worker-b queue lag
```

### DMs

DMs are too broad to be the semantic boundary.

```text
Slack DM with Aaron
    -> hard scope: user:aaron
    -> soft scope: TextForge / travel / family / marketing depending on prompt
```

This means DMs need:

- broad hard scope
- narrow soft scope
- topic drift handling over time

DMs should heavily prefer speaker-profile memories and explicit named entities
over generic channel-like priors.

## Topic Drift And Thread Titles

Thread titles or topic labels are useful as soft-scope hints.

They should:

- bootstrap soft scope early
- bias retrieval toward the current topic
- not override the hard security boundary

### Recommended behavior

- initial title can come from the first prompt
- internal soft scope can be refined over later turns
- UI title may stay stable while retrieval scope evolves

## Channel And User Profiles

Over time, the curator should build learned profiles for channels and users.

Examples:

- alert channel profile:
  - `incident_recovery`
  - service names
  - dashboards
- user DM profile:
  - `travel_profile`
  - project anchors
  - family/preferences

These learned profiles should bias retrieval, not replace hard scope.

In practice, user-profile memory is often a stronger retrieval prior than
channel history, especially for stable preferences, habits, and recurring
personal contexts.

## Write-Time Metadata Responsibilities

The write-time extractor becomes critical in this architecture.

It should emit enough structure for deterministic retrieval to work later.

### Minimum write-time metadata

- memory class
- anchor
- aliases
- facets
- optional bundle slots
- sparse relations

### Why write time matters

- write time is less latency-sensitive
- semantic work can be done once
- read time stays deterministic and fast

## Proposed Storage Direction

The existing SQLite model is sufficient as a base:

- `memory_anchors`
- `memory_documents`
- `memory_edges`

What should grow over time:

- stored aliases
- stored facets
- optional slot metadata
- relations between anchors/documents
- learned channel/user profile tables or documents

## Explainability Requirements

The deterministic stack should remain explainable at every stage.

Useful debug views:

- request plan
- candidate set
- ranked hits with reasons
- bundle slots
- inferred neighbors

```text
Prompt
  -> Request plan
      -> Candidate set
          -> Ranked hits
              -> Bundle
```

This is one of the biggest advantages over a planner-sidecar hot path.

## Production Rollout Plan

### Phase 1

- keep existing storage
- add deterministic request planning
- add cheap candidate selection
- run deterministic reranker behind a feature flag

### First Minimal Production Slice

Before replacing the current hot path, implement only the deterministic request
planning layer and log its outputs.

That slice should:

- resolve hard scope from runtime metadata
- derive soft scopes from prompt/title/entities
- choose retrieval mode (`ranked` vs `bundle`)
- emit lexical terms, anchor hints, and facets
- log the request plan for offline analysis

This gives real production signal without changing recall behavior yet.

Success criteria for the first slice:

- stable hard-scope selection in channels and DMs
- entity activation for prompts like `TextForge`, `Stir Trek`, `IAH`, and `United`
- correct mode selection for direct vs composite prompts
- low-noise request plans on generic prompts that should not strongly activate memory

### Phase 2

- enrich write-time extraction metadata
- add stored facets and slot metadata
- add learned channel/user profiles

### Phase 3

- evaluate whether any LLM reranking is still needed
- if used, keep it optional and off the hot path

## Architecture Diagram

```text
                +-----------------------------+
                | Slack / Gateway / Session   |
                +-------------+---------------+
                              |
                              v
                +-----------------------------+
                | Tier 0: Hard Scope          |
                | workspace/channel/thread/dm |
                +-------------+---------------+
                              |
                              v
                +-----------------------------+
                | Tier 1: Request Planner     |
                | prompt + title + context    |
                +-------------+---------------+
                              |
                     RetrievalRequestPlan
                              |
                              v
                +-----------------------------+
                | Tier 2: Candidate Selector  |
                | SQLite coarse narrowing     |
                +-------------+---------------+
                              |
                      candidate docs + edges
                              |
                              v
                +-----------------------------+
                | Tier 3: Deterministic       |
                | Reranker / Bundle Builder   |
                +-------------+---------------+
                              |
                              v
                +-----------------------------+
                | Injected Recall Set         |
                +-----------------------------+
```

## What This Replaces

This architecture reduces reliance on:

- per-turn LLM recall planning
- LLM-generated search queries in the hot path
- fragile planner JSON contracts for basic recall

It does not forbid LLM assistance entirely. It simply moves LLMs away from the
critical read path and toward write-time extraction or optional post-filter
reranking.

## Recommendations

1. Treat hard scope as runtime-owned metadata.
2. Use thread title/topic only as a soft retrieval boundary.
3. Invest in write-time aliases/facets/slots.
4. Make candidate selection cheap and deterministic.
5. Support both ranked and bundle retrieval.
6. Keep the retrieval path explainable.
7. Only keep LLM assistance where deterministic methods clearly fail.

## Related Artifacts

- `docs/research/memory-retrieval-scenarios.md`
- `src/Netclaw.MemoryRetrievalPoC.Tests/`
- `src/Netclaw.MemoryRetrievalPoC.Tests/Prototype/DeterministicRecallEngine.cs`
- `src/Netclaw.MemoryRetrievalPoC.Tests/Prototype/ScopeRequestPlanner.cs`
- `src/Netclaw.MemoryRetrievalPoC.Tests/Prototype/CandidateSelector.cs`
