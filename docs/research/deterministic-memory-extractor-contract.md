# Deterministic Memory Extractor Contract

Date: 2026-03-11
Status: Proposed write-time contract for deterministic retrieval

## Purpose

This document defines the minimum write-time metadata contract needed to make
deterministic memory retrieval viable in Netclaw.

The key idea is simple:

- read time should stay cheap and deterministic
- write time should produce enough stable structure for retrieval to work

This contract is the missing bridge between the memory PoCs and a production
implementation.

## Problem Statement

The deterministic retrieval PoCs show that read-time ranking can work when the
memory corpus exposes enough structure.

That structure does not appear by accident. A write-time extractor must decide:

- whether something is worth storing
- what class of memory it is
- what anchor or concept it belongs to
- what aliases help future lexical retrieval
- what coarse facets describe its retrieval neighborhood
- whether it participates in a bundle slot

Without that structure, the retrieval path becomes noisy, expensive, or too
dependent on heuristic rules.

## Contract Goals

The extractor contract should be:

- small enough to emit reliably
- stable enough for deterministic retrieval
- easy to validate in tests
- explicit enough to support explanation/debugging

## Minimum Output Model

Each extracted memory proposal should contain at least:

- operation
- memory class
- subject kind/value
- anchor
- title
- content
- aliases
- facets
- optional slots
- optional sparse relations
- recall mode
- sensitivity
- confidence
- freshness / expiry
- rationale

## Example Shape

```json
{
  "proposals": [
    {
      "operation": "upsert_document",
      "memory_class": "durable_fact",
      "subject_kind": "user",
      "subject_value": "self",
      "anchor": {
        "canonical_name": "user-travel-airline",
        "anchor_type": "preference"
      },
      "title": "Travel Profile: Preferred Airline",
      "content": "Preferred airline is United Airlines because status benefits matter.",
      "aliases": [
        "preferred airline",
        "travel preference",
        "usually fly",
        "united airlines",
        "status with united"
      ],
      "facets": ["travel_profile", "user_preference"],
      "slots": ["preferred_airline"],
      "relations": [
        {
          "relation_type": "related_to",
          "target_anchor": {
            "canonical_name": "user-travel-origin",
            "anchor_type": "preference"
          }
        }
      ],
      "recall_mode": "auto",
      "sensitivity": "normal",
      "confidence": 0.96,
      "freshness_at_ms": 1773180000000,
      "expires_at_ms": null,
      "rationale": "Stable user preference stated explicitly."
    }
  ]
}
```

## Field Semantics

### `operation`

- `upsert_document`
- `append_record`

Use `upsert_document` for stable mergeable memory.
Use `append_record` for evidence and time-bounded findings.

### `memory_class`

- `durable_fact`
- `evidence`
- `trace`

This is the strongest write-time policy classification.

### `subject_kind` / `subject_value`

These fields describe who or what the memory is about.

Examples:

- `user` / `self`
- `project` / `textforge`
- `event` / `stirtrek-2026`

They help keep anchors stable and avoid arbitrary concept drift.

### `anchor`

Anchors should be stable concept identifiers, not sentence fragments.

Good examples:

- `user-travel-origin`
- `user-travel-airline`
- `stirtrek-2026-travel-plan`
- `worker-b-queue-lag`

Anchors are the bridge between memory storage and deterministic retrieval.

### `aliases`

Aliases are critical.

They should capture natural phrasings a user might later use.

Examples:

- `fly out of`
- `home airport`
- `preferred airline`
- `queue lag`
- `hotel near venue`

Aliases are one of the highest-value write-time outputs.

### `facets`

Facets are coarse retrieval neighborhoods.

Recommended initial vocabulary:

- `travel_profile`
- `trip_planning`
- `incident_recovery`
- `rollout_guardrail`
- `deployment_reference`
- `venue_area`
- `user_preference`
- `project_fact`

Facets should stay coarse and reusable.

### `slots`

Slots are for bundle retrieval.

Examples:

- `origin_airport`
- `preferred_airline`
- `trip_plan`
- `venue_area`
- `recovery_action`
- `reference_dashboard`

Slots should be sparse and purposeful.

### `relations`

Relations are optional, sparse graph hints.

Only emit them when confidence is high.

Examples:

- related stable preferences
- event/trip support link
- recovery action -> reference dashboard relation

### `recall_mode`

Recommended default mapping:

- `durable_fact` -> `auto`
- `evidence` -> `searchable`
- `trace` -> `never`

### `sensitivity`

At minimum:

- `normal`
- `secret`

Secret items must never auto-recall.

### `confidence`

Confidence is the extractor’s confidence in the proposal quality, not an
absolute truth score.

### `freshness_at_ms` / `expires_at_ms`

These matter most for `evidence` and `trace`.

## Extraction Heuristics

The extractor should favor:

- stable explicit preferences
- repeated facts
- high-value project decisions
- event-specific planning notes when clearly useful
- verified tool findings as evidence

The extractor should avoid:

- small talk
- one-off filler
- weakly supported guesses
- noisy duplicate fragments

## Example Mappings

### Example 1: Travel Origin

Input:

`I always fly out of IAH.`

Expected output characteristics:

- `memory_class = durable_fact`
- anchor: `user-travel-origin`
- aliases include:
  - `fly out of`
  - `home airport`
  - `IAH`
- facets include:
  - `travel_profile`
  - `user_preference`
- slot:
  - `origin_airport`

### Example 2: Preferred Airline

Input:

`I prefer flying United Airlines because I have status with them.`

Expected output characteristics:

- `memory_class = durable_fact`
- anchor: `user-travel-airline`
- aliases include:
  - `preferred airline`
  - `usually fly`
  - `United Airlines`
  - `status with United`
- facets include:
  - `travel_profile`
  - `user_preference`
- slot:
  - `preferred_airline`

### Example 3: Stir Trek Travel Advice

Input:

`Best fit is a direct United flight from IAH to CMH, hotel at Easton, likely no rental car.`

Expected output characteristics:

- usually `memory_class = evidence`
- anchor: `stirtrek-2026-travel-plan`
- facets include:
  - `trip_planning`
- slot:
  - `trip_plan`
- expiry is allowed

## Relation To Retrieval

The deterministic retrieval architecture depends on this contract.

### Read-time uses

- aliases for lexical hooks
- anchors for concept activation
- facets for neighborhood grouping
- slots for bundle assembly
- relations for sparse graph propagation

### If this contract is weak

- read-time logic becomes heuristic-heavy
- retrieval quality falls
- bundle assembly becomes brittle

### If this contract is strong

- candidate filtering is easier
- reranking is simpler
- bundle retrieval becomes more reliable

## First Production Slice

The first production slice should not replace the hot path immediately.

Recommended rollout:

1. add extractor output logging/validation behind a feature flag
2. persist aliases/facets/slots in storage
3. build tests for extractor output against the scenario bank
4. only then wire deterministic retrieval to consume that metadata

## Suggested Validation Strategy

Validate the extractor on:

- travel preferences
- project-named prompts like TextForge
- event/trip planning prompts like Stir Trek
- incident recovery vs dashboard reference cases
- privacy and secret suppression

## Recommendations

1. keep the contract minimal and stable
2. prefer strong aliases over many weak fields
3. keep facet vocabulary small at first
4. use slots only for clearly bundle-worthy memory types
5. keep relations sparse and high-confidence
6. regression test the extractor independently of the retrieval engine

## Related Artifacts

- `docs/research/deterministic-memory-retrieval-architecture.md`
- `docs/research/memory-retrieval-scenarios.md`
- `src/Netclaw.MemoryRetrievalPoC.Tests/`
