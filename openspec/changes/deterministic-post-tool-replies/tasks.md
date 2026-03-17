## 1. Session evidence capture and empty-response detection

- [x] 1.1 Extend active-turn state in `LlmSessionActor` to retain a bounded, ordered set of usable evidence from successful tool results.
- [x] 1.2 Classify empty post-tool completions separately from provider exceptions, including a bounded retry/threshold path for repeated empty finalization attempts.
- [x] 1.3 Ensure the new degraded-finalization path reuses existing terminal turn completion/output contracts instead of introducing adapter-specific recovery signals.

## 2. Deterministic evidence-backed completion

- [x] 2.1 Implement deterministic best-effort reply synthesis from retained tool evidence once the persistent empty-response threshold is reached.
- [x] 2.2 Preserve the existing generic provider-failure outcome when no usable evidence exists for fallback synthesis.
- [x] 2.3 Persist and emit the synthesized reply through the normal completed-turn pathway so it behaves like any other terminal assistant text.

## 3. Verification and docs

- [x] 3.1 Add actor tests covering successful-tool evidence fallback, bounded empty-response retries, and the no-evidence generic-failure path.
- [x] 3.2 Add integration coverage showing subscribers such as Slack observe the synthesized result as ordinary visible session text, without Slack-owned synthesis logic.
- [x] 3.3 Update implementation-facing docs or release notes for the new post-tool empty-response recovery semantics and validate the change with `openspec validate deterministic-post-tool-replies --type change --strict`.
