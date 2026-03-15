## 1. SQLite Substrate And Policy Model

- [x] 1.1 Add the dedicated SQLite memory database, schema migrator, and health surface under `~/.netclaw/memory/`.
- [x] 1.2 Implement repositories for anchors, documents, records, edges, and pending checkpoints with policy metadata fields.
- [x] 1.3 Define the new SQLite-first memory configuration and explicitly leave legacy provider-mode compatibility/import out of MVP scope.

## 2. Automatic Recall In Session Turns

- [x] 2.1 Implement the pre-turn recall coordinator with bounded query/ranking logic and policy-aware filtering.
- [x] 2.2 Inject the automatic recall bundle into session turn assembly and degrade safely on timeout or storage failure.
- [x] 2.3 Update prompt/context guidance so the frontline model treats memory recall as automatic and explicit memory tools as manual-control paths.

## 3. Checkpoint Detection And Background Curation

- [x] 3.1 Implement checkpoint detection for turn completion, explicit memory requests, tool findings, compaction boundaries, and accepted subagent findings.
- [x] 3.2 Implement rules-first candidate extraction, duplicate suppression, and policy gating before any curator LLM call.
- [x] 3.3 Implement the background curation worker with retryable checkpoint recovery and atomic durable writes.

## 4. Explicit Memory Tools And Compatibility Layer

- [x] 4.1 Rewire `find_memories`, `get_memories`, `store_memory`, and `update_memory` to the SQLite memory service and policy pipeline.
- [x] 4.2 Implement document-vs-record update semantics in the explicit tool paths, including supersede and tombstone behavior.
- [x] 4.3 Preserve `find_memories`/`get_memories`/`store_memory`/`update_memory` as explicit manual-control paths while leaving legacy file-backed and Memorizer-backed provider modes out of MVP.

## 5. Subagent Ownership Changes

- [x] 5.1 Change the subagent result contract to return structured findings envelopes without default durable-memory write access.
- [x] 5.2 Route accepted subagent findings through the owning session's checkpoint pipeline.
- [x] 5.3 Add audit and observability coverage for accepted, deferred, rejected, and retried subagent-originated memory candidates.

## 6. Prompt Guidance, Skills, And Evaluation

- [x] 6.1 Update system guidance artifacts that mention memory behavior, including `netclaw-memory` and `memorizer-usage`, to reflect automatic recall as primary and explicit tools as deliberate/manual control paths.
- [x] 6.2 Create the seeded eval suite and operational checks for recall quality, noise suppression, privacy behavior, and latency thresholds from this change.
- [x] 6.3 Add a local Ollama eval profile using smaller models as the default gate for memory recall/curation quality before larger-model validation.

## 7. Spec And Operator Surface Updates

- [x] 7.1 Sync the final `netclaw-agent-memory`, `netclaw-session`, and `netclaw-subagents` specs once implementation details settle.
- [x] 7.2 Update operator-facing docs and diagnostics to explain SQLite memory health, pending checkpoints, automatic recall behavior, and deliberate manual memory tool usage.
