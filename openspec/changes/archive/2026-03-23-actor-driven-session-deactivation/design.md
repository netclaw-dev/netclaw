## Context

`SessionPipeline` currently reports `OnSessionEnded` when one materialized output stream completes, while `LlmSessionActor` already owns the real passivation decision through `ReceiveTimeout`. That mismatch makes the catalog vulnerable to false inactive transitions whenever one subscriber disconnects from a still-live multi-subscriber session, and it also refreshes `last_activity` when a pipeline is recreated for resume.

Issue `#326` will eventually need a richer drain/passivation path for graceful daemon restart, but this change only needs to make the existing active/inactive signal actor-owned and trustworthy.

## Goals / Non-Goals

**Goals:**
- Make session deactivation follow the `LlmSessionActor` idle-timeout/passivation path.
- Keep catalog status limited to `active` and `inactive`.
- Preserve `last_activity` for existing sessions unless real session output occurs.
- Add focused tests that cover both the actor callback and catalog timestamp behavior.

**Non-Goals:**
- Implement graceful drain/tombstone behavior for daemon restart.
- Add new persistence tables or schema columns.
- Introduce a new actor behavior state for idle timeout.

## Decisions

- **Use a deactivation callback from `LlmSessionActor`.**
  The actor is the canonical owner of passivation. Adding an `OnSessionDeactivated(SessionId)` callback keeps activation/deactivation tied to real actor availability instead of stream topology.
  Alternative considered: keep using pipeline teardown and count subscribers in the catalog. Rejected because it duplicates actor state and still misses actor-owned passivation semantics.

- **Remove deactivation from `SessionPipeline` termination.**
  `SessionPipeline` should still report creation and output because those events are channel-facing and cheap to observe, but output-stream completion is not a reliable signal that the session became unavailable.
  Alternative considered: leave both hooks in place. Rejected because that reintroduces races between stream shutdown and actor passivation.

- **Do not rewrite `last_activity` during status-only transitions.**
  `last_activity` should continue to mean real turn/output activity so session lists and stats remain trustworthy.
  Alternative considered: update `last_activity` whenever a session becomes active/inactive. Rejected because resume/disconnect churn would make dormant sessions look recently active.

- **Standardize on `inactive` instead of `idle`.**
  The immediate need is a binary availability signal. `inactive` matches the actor-owned deactivation semantics and leaves room for a future explicit drain state if issue `#326` needs it.

## Risks / Trade-offs

- **Inactive transition waits for idle timeout** -> sessions remain `active` for the configured timeout after the last subscriber leaves. This is acceptable because the actor is still live and can accept new turns during that window.
- **Future graceful drain still needs another entry point** -> issue `#326` must add an explicit drain/deactivate path, but it can reuse the same observer callback introduced here.
- **Existing rows may still contain `idle`** -> old data is tolerated because active-session counts only rely on `status = 'active'`; rows normalize to `active` / `inactive` once touched again.
