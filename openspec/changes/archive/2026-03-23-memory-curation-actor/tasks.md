## 1. MemoryCurationActor skeleton

- [ ] 1.1 Create `MemoryCurationActor` as a `ReceiveActor` in `Netclaw.Actors/Memory/` with `Idle`, `Evaluating`, and `Writing` behaviors
- [ ] 1.2 Define message protocol: `EvaluateProposals` (input from session), `CurationResult` (internal), `CurationCompleted`/`CurationFailed` (reply)
- [ ] 1.3 Accept `SQLiteMemoryStore` and `IChatClientProvider?` via constructor injection (store for queries, client for LLM tier)
- [ ] 1.4 Wire creation in `LlmSessionActor.PreStart` via `Context.ActorOf` — store reference as `_curationActor` field

## 2. Fuzzy anchor matching

- [ ] 2.1 Create `AnchorNameMatcher` utility: tokenize anchor names on `-`, compute Jaccard similarity, detect subset matches
- [ ] 2.2 Add `FindFuzzyAnchorMatchesAsync` to `SQLiteMemoryStore` — query all anchors in domain, run fuzzy match against proposed name, return candidates
- [ ] 2.3 Unit tests for `AnchorNameMatcher`: exact match, subset match, single-token-diff match, no match on unrelated names, version-suffix match

## 3. Rules-tier evaluation

- [ ] 3.1 Create `CurationRulesEvaluator` with `Evaluate(proposal, existingCandidates)` returning `CurationDecision` (Skip/Update/Consolidate/Create + target IDs)
- [ ] 3.2 Implement exact anchor match + content overlap check (>80% token overlap → Skip)
- [ ] 3.3 Implement exact anchor match + different content + fresher timestamp → Update (return existing document ID)
- [ ] 3.4 Implement fuzzy anchor match found → return Consolidate candidate set for LLM tier or auto-consolidate if content overlap is high
- [ ] 3.5 Implement no match → Create
- [ ] 3.6 Immutable records (`MemoryKind.Record`) bypass evaluation, always Create
- [ ] 3.7 Unit tests for each decision path with fixture anchors and documents

## 4. LLM-tier evaluation

- [ ] 4.1 Create `CurationPromptBuilder` with system prompt and user message template (from design doc Decision 7)
- [ ] 4.2 Implement LLM call in curation actor `Evaluating` behavior: invoke compaction model with reasoning disabled, 10s timeout
- [ ] 4.3 Parse single-keyword response (SKIP/UPDATE/CONSOLIDATE/CREATE) + optional memory IDs
- [ ] 4.4 Fallback: on timeout or parse failure, use rules-tier best-effort decision
- [ ] 4.5 Unit tests for prompt builder output format and response parsing

## 5. Write operations

- [ ] 5.1 Implement Skip decision: log and discard, no DB write
- [ ] 5.2 Implement Update decision: call `ApplyCurationBatchAsync` with existing document ID so `ON CONFLICT UPDATE` fires
- [ ] 5.3 Implement Consolidate decision: pick canonical anchor, re-anchor documents from redundant anchors, tombstone redundant anchors via `TombstoneDocumentAsync`
- [ ] 5.4 Implement Create decision: call `ApplyCurationBatchAsync` with new document ID (existing behavior)
- [ ] 5.5 Add consolidation logging: log anchor merges with before/after state for operator review

## 6. Wire into LlmSessionActor

- [ ] 6.1 In `MemoryObservationCompleted` handler: send accepted proposals to `_curationActor` via `Tell` instead of `EnqueueCheckpointFireAndForget` for `ObservedMemoryProposals`
- [ ] 6.2 Remove `ObservedMemoryCheckpointPayload` enqueue path from session actor — observed proposals no longer go through checkpoint queue
- [ ] 6.3 Keep checkpoint enqueue for other trigger types (turn-complete, explicit-request, compaction-boundary, subagent-findings)
- [ ] 6.4 Verify `MemoryCurationWorkerService` still drains non-observed checkpoints (turn-complete, etc.) via existing `CurateAsync` + `ApplyCurationBatchAsync` path

## 7. Testing

- [ ] 7.1 Integration test: send duplicate proposal to curation actor, verify single document in store
- [ ] 7.2 Integration test: send updated content for same anchor, verify document updated in place (same ID, new content)
- [ ] 7.3 Integration test: send proposals with fuzzy-matching anchor names, verify consolidation
- [ ] 7.4 Integration test: send immutable record, verify it bypasses evaluation and creates
- [ ] 7.5 Integration test: LLM timeout falls back to rules decision without blocking

## 8. Eval and spec sync

- [ ] 8.1 Run eval suite — verify memory_formation and memory_recall cases still pass
- [ ] 8.2 Inspect memory database after eval: verify zero same-anchor duplicates, reduced anchor fragmentation
- [ ] 8.3 Update `netclaw-memory` system skill if curation behavior changes affect agent guidance
- [ ] 8.4 Sync delta spec to main spec via `/opsx-sync`
