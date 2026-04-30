## Context

Netclaw currently treats memory as a backend choice: markdown files under `~/.netclaw/memories/` or an optional Memorizer-backed provider discovered through MCP. That model was enough to prove out persistence, but it is not enough for PRD-007's actual job: durable recall that is local-first, policy-aware, structurally organized, and reliable across long-running sessions.

The current architecture has four core limits:

1. Recall is mostly model-driven. If the frontline agent does not explicitly decide to call memory tools, recall does not happen.
2. The default file store is flat. It can hold notes, but not entities, parent/child structure, freshness, or typed relationships.
3. Durable memory policy is implicit. Domain separation, sensitivity, and update behavior are not encoded on each memory item.
4. Subagents can become accidental durable-memory writers, which makes quality, privacy, and ownership harder to reason about.

This redesign makes memory a first-party Netclaw subsystem with its own local SQLite database, policy model, retrieval pipeline, and background curation queue. The Akka session layer remains the owner of turn orchestration, but durable memory is no longer a thin wrapper around files or an optional MCP dependency.

## Goals / Non-Goals

**Goals:**
- Make local SQLite the default and normative durable memory substrate for Netclaw.
- Represent memory around anchors/entities, hierarchy, and graph links instead of flat files.
- Distinguish mutable `documents` from immutable `records` with explicit update semantics.
- Make recall automatic before each user-facing turn, bounded by policy and latency budgets.
- Move durable persistence into checkpoint-driven background curation with deterministic filtering before any LLM curator call.
- Keep the main session as the durable-memory owner; subagents return findings, not writes.
- Preserve a small explicit tool surface for deliberate save/search/correct workflows so prompts and skills can distinguish automatic recall from manual memory control.
- Provide measurable success criteria and eval gates before the redesign becomes the only supported path.

**Non-Goals:**
- Embedding or vector retrieval in MVP-now.
- Bi-directional live sync with Memorizer as a primary store.
- Replacing the existing 4 explicit memory tools with a brand-new graph-native tool API in MVP-now.
- Legacy markdown import or provider-compatibility work as a required MVP deliverable.
- Full encrypted-at-rest key management beyond host filesystem controls.
- Automatic cross-device memory replication.
- General-purpose knowledge graph authoring UI.

## Decisions

### Decision: Use a dedicated SQLite memory database, separate from Akka persistence

Netclaw will store durable memory in a dedicated SQLite database under `~/.netclaw/memory/netclaw-memory.db` rather than markdown files or Akka persistence tables.

Rationale:
- Keeps memory schema evolution independent from Akka journal/snapshot concerns.
- Supports indexed structured queries, parent/child traversal, and typed edges without introducing another service dependency.
- Preserves local-first operation on `pi1` and in tests.

Alternatives considered:
- Reuse `~/.netclaw/memories/` markdown files. Rejected because flat files cannot efficiently support policy-scoped graph traversal, freshness, or background checkpoint queues.
- Make Memorizer the default substrate. Rejected because durable core memory should not depend on optional MCP reachability.
- Store memory in the Akka journal. Rejected because memory objects need their own query/index lifecycle and should not be coupled to framework-owned event persistence.

### Decision: Model durable memory as anchors plus documents, records, and edges

The core data model is:

- `anchors`: canonical entities or topics (`person`, `project`, `repo`, `service`, `host`, `preference`, `task`, `concept`) with optional parent anchor.
- `documents`: mutable living summaries attached to an anchor, such as operator preferences, project briefs, or evolving runbooks.
- `records`: immutable observations or events attached to an anchor, such as "router IP changed" or "issue #164 fixed on 2026-03-07".
- `edges`: typed graph relationships between anchors (`depends_on`, `owned_by`, `runs_on`, `related_to`, `child_of`).

Each durable object carries policy metadata: `domain`, `sensitivity`, `recallMode`, `confidence`, `freshness`, and `updateSemantics`.

Rationale:
- Documents and records need different write rules.
- Anchors let Netclaw recall by entity and traverse context without flattening everything into note blobs.
- Typed edges make hierarchy and graph recall possible without full graph-database complexity.

Alternatives considered:
- Single table of notes with tags. Rejected because tags are too weak for hierarchy, relationships, and update rules.
- Fully normalized graph-only model. Rejected for MVP because documents still need rich markdown/text bodies and simple operator-facing editing.

### Decision: Make recall system-driven, not model-optional

Before each user-facing turn, the session will run a bounded automatic recall pipeline and inject a structured recall bundle into the prompt/context. Explicit memory tools remain available, but they are not the primary recall mechanism.

Rationale:
- Fixes the main failure mode where the model forgets to search.
- Makes recall quality measurable independently of model whim.
- Lets policy filtering happen before prompt injection.

Alternatives considered:
- Keep explicit tool-driven recall only. Rejected because it is unreliable and too dependent on prompt compliance.
- Inject the entire memory index every turn. Rejected because it is noisy and token-expensive.

### Decision: Use rules-first candidate extraction before any curator LLM call

Checkpoint curation starts with deterministic extraction and filtering rules. The curator LLM only processes candidates that survive policy checks, dedupe checks, and heuristic suppression.

Rationale:
- Cuts latency and noise.
- Keeps obviously sensitive, stale, or trivial content away from the curator.
- Reduces reward-hacking pressure to store everything.

Alternatives considered:
- LLM-only memory extraction. Rejected because it is expensive, less predictable, and prone to saving low-value text.
- Rules-only persistence. Rejected because durable normalization still benefits from a curator on ambiguous or merge-heavy cases.

### Decision: Main session owns durable writes; subagents return findings envelopes

Subagents will not persist durable memory directly by default. Instead they return a structured findings envelope to the parent session, and the parent session decides whether those findings become checkpoints and durable writes.

Rationale:
- Preserves a single policy and ownership boundary for durable memory.
- Prevents specialized subagents from creating unsupervised long-term state.
- Makes audit, attribution, and rollback much easier.

Alternatives considered:
- Allow all subagents to call durable memory tools directly. Rejected because it spreads trust and persistence policy across too many actors.
- Forbid subagent findings entirely. Rejected because subagents still need to contribute research and summaries.

### Decision: Preserve the existing 4 explicit memory tools as deliberate manual control paths in MVP-now

The frontline agent will continue to see `find_memories`, `get_memories`, `store_memory`, and `update_memory`, but they become thin facades over the SQLite memory service and policy pipeline.

Rationale:
- Minimizes prompt churn and skill breakage.
- Keeps explicit workflows available for "remember this", "search memory", and "correct this note" requests.
- Makes it clear that automatic recall is the default while explicit tools are only for deliberate operator- or agent-driven control.

Alternatives considered:
- Introduce new graph-native memory tools immediately. Rejected for MVP-now because automatic recall is the bigger value and compatibility matters.
- Remove explicit memory tools entirely. Rejected because operators still need deliberate control paths.

## Architecture

### Components

1. `MemorySchemaMigrator`
   - Creates/upgrades `netclaw-memory.db`
   - Tracks schema version and health

2. `MemoryRepository`
   - CRUD/query methods for anchors, documents, records, edges, and pending checkpoints
   - Enforces update semantics (`merge-document`, `append-document`, `supersede-record`, `immutable-record`, `tombstone`)

3. `MemoryPolicyEvaluator`
   - Applies domain separation and sensitivity rules on write and recall
   - Produces deny/degrade reasons for diagnostics

4. `MemoryRecallCoordinator`
   - Runs on every user-facing turn before the model call
   - Produces a compact recall bundle from SQLite under a strict time budget

5. `CheckpointDetector`
   - Examines turn completions, explicit memory requests, compaction events, tool outputs, and accepted subagent findings
   - Emits checkpoint intents into a durable queue

6. `MemoryCurationWorker`
   - Background actor/service that consumes pending checkpoints
   - Runs rules-first extraction, optional curator normalization, and durable writes
   - Retries pending work after restart

7. `MemoryToolFacade`
   - Keeps the existing 4 explicit tools alive as a compatibility surface
   - Routes all writes through the same policy + checkpoint pipeline

### Data Shape

Illustrative schema:

```text
anchors(
  anchor_id,
  anchor_type,
  canonical_name,
  parent_anchor_id,
  domain,
  sensitivity,
  recall_mode,
  confidence,
  freshness_at,
  status,
  created_at,
  updated_at
)

documents(
  document_id,
  anchor_id,
  title,
  markdown_body,
  update_semantics,
  confidence,
  freshness_at,
  created_at,
  updated_at
)

records(
  record_id,
  anchor_id,
  record_type,
  payload_json,
  supersedes_record_id,
  confidence,
  freshness_at,
  created_at
)

edges(
  edge_id,
  from_anchor_id,
  to_anchor_id,
  relation_type,
  confidence,
  freshness_at,
  created_at,
  updated_at
)

checkpoints(
  checkpoint_id,
  session_id,
  turn_id,
  trigger_type,
  priority,
  status,
  payload_json,
  created_at,
  updated_at,
  retry_count
)
```

### Example Populated Knowledge Graph

```text
[person:aaron]
  |-- document: operator-preferences
  |     `-- tone=concise, timezone=America/Chicago
  |-- related_to --> [project:netclaw]
  `-- owns --> [host:pi1]

[project:netclaw]
  |-- document: project-brief
  |-- child_of --> [domain:business]
  |-- contains --> [repo:netclaw-dev/netclaw]
  |-- related_to --> [service:slack-adapter]
  `-- related_to --> [concept:memory-policy-graph-redesign]

[repo:netclaw-dev/netclaw]
  |-- document: current-focus
  |-- depends_on --> [service:sqlite-memory]
  `-- record: issue-164-fixed@2026-03-07

[host:pi1]
  |-- document: homelab-inventory
  `-- runs_on --> [service:netclaw-daemon]

[service:sqlite-memory]
  |-- document: schema-notes
  |-- record: migration-disabled-for-mvp@decision
  `-- related_to --> [concept:automatic-recall]
```

### Policy Model

Every durable memory candidate and stored object includes:

- `domain`: `personal`, `business`, `homelab`, `project:<slug>`, or another configured domain
- `sensitivity`: `normal`, `restricted`, `secret`
- `recallMode`: `auto`, `manual`, `never`
- `confidence`: `0.0-1.0`
- `freshness`: timestamps plus optional expiry window
- `updateSemantics`: `merge-document`, `append-document`, `supersede-record`, `immutable-record`, `tombstone`

Default behavior:
- Cross-domain recall is deny-by-default unless a policy explicitly allows it.
- `secret` memories are never auto-injected.
- `manual` memories are available only via explicit tool calls.
- Stale low-confidence items lose ranking and can be filtered out entirely.

### Tool Invocation Model

What the frontline agent sees:
- `find_memories`: targeted manual search when automatic recall was insufficient or the user explicitly asks what Netclaw remembers
- `get_memories`: hydrate selected memories in full
- `store_memory`: explicit remember/save request; routes through checkpoint pipeline instead of writing directly
- `update_memory`: document correction, record supersede, tombstone, or recall-mode adjustment

What is automated by the system:
- pre-turn recall before each user-facing model turn
- checkpoint detection after eligible events
- rules-first candidate extraction
- background curator processing
- policy filtering on every recall and persistence path

When explicit memory tools are still used:
- user explicitly says "remember this", "forget this", or "what do you remember about X"
- the agent needs manual follow-up beyond the automatic recall bundle
- operator correction or cleanup workflows need a direct edit/supersede path

When the frontline agent should explicitly invoke `store_memory`:
- the user directly asks Netclaw to remember or save something
- the agent wants to deliberately pin a high-value fact, decision, or preference instead of waiting for background checkpoint curation
- a workflow requires immediate durable capture with an acknowledgment back to the user

When the frontline agent should explicitly invoke `update_memory`:
- the user corrects an existing preference, fact, or project summary
- a prior record needs supersede/tombstone handling
- sensitivity, recall mode, or other durable metadata must be intentionally changed

When the frontline agent should not use explicit write tools:
- routine user turns where automatic recall already provides what is needed
- background curation opportunities detected by the system after the turn
- speculative or low-confidence facts that should first pass through checkpoint filtering

### System Prompt And Memory Context Guidance

Session prompt guidance should teach a simple operating model:

- automatic recall is primary and happens before each user-facing turn
- explicit memory tools are manual control paths, not the default way to use memory
- `store_memory` is for deliberate remember/save actions
- `update_memory` is for correction, supersede, tombstone, or metadata changes
- if memory is degraded, the prompt must say so plainly instead of implying recall is active

Illustrative context guidance:

```text
[memory]

Netclaw automatically recalls durable memory before each user-facing turn.
Assume relevant durable context may already be present in the recall bundle.

Use `find_memories` / `get_memories` only when you need manual follow-up beyond
the automatic bundle or the user explicitly asks what you remember.

Use `store_memory` only for deliberate save/remember actions.
Use `update_memory` only to correct, supersede, tombstone, or change metadata on
existing durable memory.

Do not call explicit memory tools as a routine reflex on every turn.
```

### Actor Boundaries and Recovery Behavior

- `LlmSessionActor` remains responsible for turn orchestration.
- Automatic recall is synchronous but time-bounded; if it fails or times out, the turn continues with degraded memory status.
- Checkpoint enqueue is synchronous only up to durable queue acknowledgment; curation itself is asynchronous.
- `MemoryCurationWorker` owns retryable background writes and resumes pending checkpoints after daemon restart.
- `SubAgentActor` stays ephemeral and non-persistent; it returns findings back to the parent session.

Failure modes and recovery:
- SQLite missing: create database and schema automatically.
- SQLite unavailable or corrupted: session turns continue without automatic recall, explicit memory tools return degraded errors, and health reports degraded memory status.
- Curator timeout/failure: checkpoint remains pending with retry metadata; no partial write is committed.
- Policy deny during recall: item is excluded silently from prompt injection and logged for diagnostics at debug/audit level.

## Pseudocode

### Checkpoint detection

```text
function detectCheckpoints(turn, toolOutputs, subagentFindings, compactionState):
  checkpoints = []

  if turn.userExplicitlyRequestsRemember or turn.userExplicitlyRequestsForget:
    checkpoints.add(highPriority("explicit-memory-request", turn))

  if turn.containsStablePreference or turn.containsDurableDecision:
    checkpoints.add(normalPriority("stable-fact", turn))

  if turn.containsProjectStateChange or turn.containsResolvedAction:
    checkpoints.add(normalPriority("project-update", turn))

  if toolOutputs.includeVerifiedExternalFact:
    checkpoints.add(normalPriority("verified-tool-finding", toolOutputs))

  if subagentFindings.acceptedByParentSession:
    checkpoints.add(normalPriority("subagent-findings", subagentFindings))

  if compactionState.nearThreshold or compactionState.completed:
    checkpoints.add(highPriority("compaction-boundary", compactionState.summary))

  return checkpoints
```

### Rules-first candidate extraction

```text
function rulesFirstExtract(checkpoint, policyContext, existingMemoryIndex):
  spans = splitIntoCandidateSpans(checkpoint.payload)
  candidates = []

  for span in spans:
    if isSmallTalk(span) or isEphemeral(span) or lacksStableReferent(span):
      continue

    candidate = classify(span)
    candidate.domain = inferDomain(span, policyContext)
    candidate.sensitivity = inferSensitivity(span, policyContext)
    candidate.anchorKey = resolveAnchorKey(candidate)

    if violatesWritePolicy(candidate, policyContext):
      continue

    if isDuplicateOrLowerQuality(candidate, existingMemoryIndex):
      continue

    if candidate.confidence < 0.55 and not checkpoint.isExplicitRequest:
      continue

    candidates.add(candidate)

  return candidates
```

### Curator processing

```text
function curateCandidates(candidates, currentGraph):
  operations = []

  for candidate in candidates:
    if candidate.isDeterministic:
      operations.add(buildDirectOperation(candidate, currentGraph))
      continue

    curated = callCuratorModel(candidate, currentGraph.summaryFor(candidate.anchorKey))

    if curated.rejected:
      continue

    operations.add(normalizeToOperation(curated))

  applyTransaction(operations)
  return operations
```

### Automatic pre-turn recall

```text
function automaticRecall(session, incomingUserMessage):
  intent = classifyIntent(incomingUserMessage, session.recentTurns)
  policyScope = buildPolicyScope(session.channel, session.sender, session.activeProject)
  query = buildRecallQuery(intent, incomingUserMessage, session.activeAnchors)

  ranked = searchSQLiteMemory(
    query = query,
    domains = policyScope.allowedDomains,
    excludedSensitivity = policyScope.autoRecallDeniedSensitivity,
    recallModes = ["auto"],
    freshnessBias = now()
  )

  bundle = compactToRecallBundle(ranked.top(5), tokenBudget = session.memoryBudget)
  return bundle
```

### Subagent findings flowing back to main session

```text
function handleSubagentResult(parentSession, subagentResult):
  if not subagentResult.success:
    return

  findings = extractFindingsEnvelope(subagentResult)

  if findings.isEmpty:
    return

  if parentSession.policy.disallowsSubagentDomain(findings.domain):
    logDrop(findings, reason = "policy")
    return

  checkpoint = buildCheckpointFromFindings(parentSession.sessionId, findings)
  enqueueCheckpoint(checkpoint)
  parentSession.emitObservation("subagent findings queued for memory review")
```

## Risks / Trade-offs

- [SQLite query complexity on constrained hardware] -> Keep schema small, use indexed fields only, and gate recall to top-N bounded queries.
- [Automatic recall may inject noise] -> Keep strict candidate limits, policy filters, and eval gates for precision before rollout.
- [Deferred legacy compatibility might leave old data unused if it exists locally] -> Treat MVP as greenfield and document that any legacy import/provider bridge is a separate follow-up concern, not a hidden partial feature.
- [Compatibility shims may preserve old habits too long] -> Mark them as explicit/manual paths in prompt guidance and keep graph-native APIs deferred behind a later change.
- [Background curation can silently fail] -> Persist checkpoints with retry state, surface queue health in diagnostics, and never report a save as complete before enqueue acknowledgment.

## Delivery Plan / Compatibility Stance

1. Add `memory/` directory, SQLite schema migrator, and health reporting.
2. Build repositories and policy evaluator for anchors, documents, records, edges, and checkpoints.
3. Add explicit memory tool facades backed by the new substrate.
4. Implement automatic recall in `LlmSessionActor` with bounded timeout and degraded fallback.
5. Implement checkpoint detection plus `MemoryCurationWorker` retry queue.
6. Change subagent contract to return findings envelopes; route accepted findings through the parent session.
7. Update system-prompt guidance, memory context guidance, and system skills so automatic recall is primary and explicit tools are manual control paths.
8. Ship the seeded eval suite and require the smaller local Ollama profile to pass before broader validation.
9. Do not require markdown import, provider-mode migration, or Memorizer-compatibility bridges for MVP. If needed later, they should be proposed as separate follow-up changes.

Rollback stance:
- Schema migrations are additive before cutover.
- Rollback can disable the redesigned memory subsystem and preserve the SQLite data directory for inspection.
- No reverse-sync or legacy import/export path is assumed in MVP.

## Dependency Map And Sequencing

This change intentionally separates the memory redesign from broader platform work that is still evolving.

### What this change depends on directly

- SQLite-backed durable storage, recall, checkpointing, and policy evaluation.
- Session-layer prompt/context injection for automatic recall.
- A parent-session-owned path for accepting or rejecting structured subagent findings.

### What this change does not need to wait for

- Issue `#150` (`Refactor skill system to adopt AgentSkills.io SKILL.md standard`)
  - Not a blocker.
  - The memory redesign only needs prompt guidance and system-skill updates explaining that automatic recall is primary and explicit memory tools are manual control paths.
  - The memory curator should remain an internal platform role in MVP, not a discoverable user skill.

- Issue `#149` (`Improve skill guidance for Playwright screenshot handoff and simplify skill routing`)
  - Not a blocker.
  - This memory redesign may improve general prompt discipline around explicit tools, but screenshot workflow routing is a separate skill-system concern.

- Issue `#147` (`Reminder UX follow-up: allow selecting agent definition for execution`)
  - Not a blocker.
  - Memory curation does not require user-selectable agent profiles.
  - The memory curator should be a platform-owned internal role with a fixed prompt/tool set, not a reminder-style user choice.

### What this change should pull forward from subagent work

- Structured findings on `SubAgentResult` rather than text-only output.
- Parent-session acceptance/rejection of findings before durable checkpoint creation.
- A safe internal invocation path for platform-owned subagents such as a future `memory-curator` role.

### Recommended staging

```text
Phase A: Memory foundation
- SQLite schema and repositories
- automatic pre-turn recall
- checkpoint queue and policy evaluation

Phase B: Session-owned curation
- rules-first candidate extraction
- deterministic document/record operations
- structured subagent findings envelopes

Phase C: Internal memory-curator role (optional if deterministic-only is insufficient)
- platform-owned internal subagent definition
- fixed prompt + narrow tool scope
- findings/operation handoff back to parent session

Phase D: Broader platform follow-ups
- reminder agent-definition UX (#147)
- AgentSkills.io skill-system refactor (#150)
- screenshot-handoff routing cleanup (#149)
```

### Recommended follow-up implementation issues

- `Add structured findings envelopes to SubAgentResult and parent-session acceptance flow`
- `Add internal platform-owned agent definition support for non-user-routable subagents`
- `Package memory curator as an internal role if deterministic checkpoint curation proves insufficient`
- `Refactor reminder execution to use agent-definition selection after internal agent-definition support stabilizes`
- `Adopt AgentSkills.io SKILL.md/frontmatter without coupling it to memory-curator implementation`

## Success and Evaluation Criteria

The implementation is not done until it passes a seeded memory eval suite with measurable outcomes:

- Eval execution baseline: run the seeded suite locally against smaller Ollama-hosted models first. Passing results on constrained local models is the primary quality gate before validating on larger hosted models.
- Recall quality: at least one relevant item appears in the automatic recall bundle for >= 85% of recall-positive test cases.
- Noise suppression: no-memory-needed cases return an empty automatic recall bundle in >= 80% of cases, and auto recall never injects more than 3 items.
- Privacy/sensitivity: blocked domain and blocked sensitivity items leak into auto recall in 0 eval cases.
- Operational latency: automatic recall p95 <= 300 ms and checkpoint enqueue p95 <= 25 ms on the standard local fixture; background curation p95 <= 5 s for a <= 10-candidate checkpoint.
- Ownership: 0 durable memory writes originate directly from a default subagent path in audit logs.

### Eval Harness Expectations

- The seeded eval suite must support a local Ollama provider profile so Netclaw can run recall and curation evaluations entirely on local hardware.
- The default pre-merge memory eval profile should target smaller Ollama models because they provide the strictest practical test of prompt efficiency, bounded recall bundles, and policy clarity.
- Larger hosted models are follow-up validation targets, not the primary design bar. The architecture should be considered acceptable only if the local Ollama profile already meets the success thresholds above.
- Eval reporting should break out at least: model ID, recall hit rate, false-positive recall rate, blocked-memory leakage count, checkpoint-to-curation latency, and average injected recall bundle size.

## Open Questions

- Whether future graph-native memory tools should replace the compatibility 4-tool surface after the eval suite proves automatic recall is stable. This is explicitly deferred, not blocking MVP-now.
