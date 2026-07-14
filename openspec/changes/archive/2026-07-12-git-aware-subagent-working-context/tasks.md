## 1. Shared Working-Context Snapshot

- [x] 1.1 Add immutable snapshot/result types and a shared audience-aware snapshot service with pure `[working-context]` rendering.
- [x] 1.2 Add bounded, non-shell Git porcelain inspection for linked worktrees, branch/HEAD/upstream/divergence, dirty counts, non-Git detection, and explicit sanitized failures.
- [x] 1.3 Register the snapshot service and integrate it at the main session's turn-start volatile-tail boundary without persisting Git state.

## 2. Subagent Context Ownership And Handoff

- [x] 2.1 Extend the spawn protocol with a read-only parent recent-file snapshot and initialize child runtime context without changing the reusable system prompt.
- [x] 2.2 Reuse canonical tool-result path tracking to maintain child read/confirmed-change state and capture start/final Git snapshots for observed changes.
- [x] 2.3 Extend structured subagent completion metadata and merge only confirmed files from successful children into parent durable working context.

## 3. Automated Proof

- [x] 3.1 Add unit tests for Git parsing/inspection, rendering, audience suppression, timeout/failure behavior, linked worktrees, and credential non-disclosure.
- [x] 3.2 Add actor/integration tests for main next-turn refresh, child inheritance/isolation, structured handoff, successful merge, failed-child non-merge, and cache-stable placement.
- [x] 3.3 Add fixture-aware targeted multi-turn coding-context eval cases with direct Git/filesystem assertions and JSON/cache metrics.

## 4. Guidance And Verification

- [x] 4.1 Update mapped system-skill guidance and eval documentation for Git-aware main/subagent working context.
- [x] 4.2 Validate OpenSpec artifacts, run targeted tests and focused eval/cache cases where a provider is available, then run Slopwatch and file-header verification.
- [x] 4.3 Verify implementation against the OpenSpec change and sync/archive the completed change.
