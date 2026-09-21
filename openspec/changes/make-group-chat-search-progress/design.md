## Context

See [proposal.md](proposal.md) for the deployed failure and the superseded manual continuation decision.
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

The Graph adapter already returns bounded metadata batches with opaque continuations.
The TUI currently stops after each batch and counts only users whose chat pages are complete.
One user with many chat pages can therefore consume several actions without an apparent count change.

## Goals / Non-Goals

**Goals:**

- Reuse the existing directory contract, query-generation guard, and Group Chat review path.
- Bound each automatic run while the terminal remains responsive.
- Expose actual work without reporting an invented tenant-completion percentage.

**Non-Goals:**

- No persistent index, daemon background scan, or new configuration setting.
- No tenant endpoint, credential flow, participant selector, or message API.
- No change to actor boundaries, canonical destination persistence, or runtime ACL consumers.

## Decisions

### Traverse metadata pages fairly

The Graph adapter owns a temporary queue of tenant-user pages and per-user chat pages.
Each successful page adds its continuation behind other queued sources.
This permits other users to receive service before one user's long chat list is complete.
The existing ten-request provider budget remains in force.

Title search reads chat IDs, topics, and chat types without member expansion.
The adapter filters group chats by a case-insensitive title substring and deduplicates canonical IDs.
Participant expansion adds work that this title match does not need.

The alternative was to increase only the provider budget.
That approach keeps source starvation and gives the UI fewer opportunities to report progress or stop.

### Run batches automatically with cumulative progress

The view model owns a temporary run with at most twenty provider batches and a two-minute cancellation deadline.
The existing provider budget limits that run to at most 200 Graph requests.
The search controller waits one cancellable second before each continuation batch.
This interval paces consecutive batches against [Graph chat-list limits](https://learn.microsoft.com/en-us/graph/throttling-limits#chats) without delaying cancellation.
The initial text query retains the existing 300-millisecond debounce.

Each successful batch supplies cumulative requests, chat records, completed users, and unavailable users.
The UI appends new matches by canonical ID and preserves the selected row.
Completed-user counts can remain unchanged while additional chat pages increase the other counters.
Counts describe the last successful checkpoint; an interrupted batch can repeat without inflating those counts.

The UI offers `Stop search` and `Ctrl+S` during the run.
A stop or automatic bound preserves matches and the last successful continuation for `Resume search`.
A complete response ends the run without another operator action.
The alternative was another mandatory continuation action after every small empty batch.
The deployed feedback rejects that interaction.

### Retain checkpoint and result authority

The adapter copies continuation state before each batch and publishes a new snapshot only after success.
Cancellation or failure cannot consume the last successful checkpoint.
The existing tenant, query, limit, and expiry checks remain in force.
The adapter detects repeated continuation URLs and bounds tracked sources and requests.
The TUI retains at most 1,000 distinct matches and requests a narrower title when that bound is exceeded.
Graph consent errors, throttles, malformed pages, and resource bounds produce explicit failures.
A user-chat 404 remains an unavailable source and keeps coverage partial.

The view model applies each batch through the UI loop after it checks the screen, query, and run generation.
Input edits, departure, or result selection cancel the old run.
Late responses cannot change the new query or the selected review value.

Schematic sequence; existing credential and canonical-ID validation remains required:

```text
submit title -> create run -> request next bounded batch
successful batch -> UI loop -> check generation -> retain matches and checkpoint
metadata remains and run within bounds -> request next batch
stop or run bound -> retain checkpoint -> Resume search
select result -> cancel run -> canonical-ID review -> existing configuration writer
```

The Graph adapter owns temporary metadata and continuation snapshots.
The view model owns temporary progress, results, and selection.
The configuration writer owns durable canonical chat IDs.
The daemon consumes those IDs through the existing Teams authorization path.
No discovered user, topic, count, or continuation becomes a durable access grant.

## Risks / Trade-offs

- [Large tenant] → Bound each run, expose cumulative work, and retain an explicit resume action.
- [Graph returns repeated pages] → Stop with a pagination error before a source loops indefinitely.
- [Stop interrupts a batch] → Resume from the last successful checkpoint; the incomplete batch can repeat.
- [Checkpoint expires] → Explain the invalid continuation and offer a fresh search.
- [Metadata lacks the expected title] → Preserve advanced canonical-ID entry and avoid a claim of complete coverage after an error.

## Migration Plan

No configuration migration is required.
Deploy the corrected binary after automated validation.
Test partial and full chat titles with the existing application consent.
Verify the selected canonical ID through the current Group Chat ingress flow.
Rollback uses the previous binary; saved IDs remain compatible.
