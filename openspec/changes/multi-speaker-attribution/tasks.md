## 1. ACL Boundary

- [x] 1.1 Remove the proposed `Observe` outcome from the change plan and keep ACL binary for executable inbound turns
- [x] 1.2 Specify authorized-only live turn creation for threaded adapters in ACL and input-adapter deltas
- [x] 1.3 Specify that unauthorized live messages do not dispatch model turns or enter downstream control paths

## 2. Authorized Sync Watermark

- [x] 2.1 Define a per-thread authorized sync watermark in the thread-history-backfill delta
- [x] 2.2 Specify watermark lower-bound and upper-bound semantics for adoption windows
- [x] 2.3 Specify adapter-owned gap fetch and watermark bookkeeping with pending-cursor persistence after enqueue acceptance and durable watermark advance only on `TurnCompleted` or durable turn completion
- [x] 2.4 Specify deterministic stale/replay handling, including same-message idempotency and adopted-record reuse

## 3. Adopted Context Window

- [x] 3.1 Replace ordinary live-history merge framing with an explicit adopted-context window
- [x] 3.2 Specify that unsynced thread messages are hydrated only when an authorized message arrives
- [x] 3.3 Specify authority-at-inclusion capture for each adopted message using the same live turn-creation authorization basis evaluated at adoption time
- [x] 3.4 Define canonical framing markers for adopted messages and the current authorized message
- [x] 3.5 Define escaping rules for reserved framing markers and explicit zero-gap omission of adopted-context framing

## 4. Adopted Context Audit Persistence

- [x] 4.1 Add a `netclaw-session` delta for persisted adopted-context records
- [x] 4.2 Specify required audit fields plus deterministic idempotency basis and reuse of existing adopted-context records for same-message retries
- [x] 4.3 Specify that the adapter may construct the canonical projection, but the session persists that exact projection plus adopted metadata before execution continues and retries/recovery reuse the persisted record
- [x] 4.4 Specify that adopted context is quoted non-executable input, only the current authorized message is executable, and persistence failure prevents enqueue

## 5. Downstream Authority Gates

- [x] 5.1 Add a `slash-command-dispatch` delta limiting slash interception to the current authorized executable message
- [x] 5.2 Add a `tool-approval-gates` delta limiting approval requests to tools caused by the current authorized executable message and requiring deterministic authorizer/adopted-speaker provenance when the adopted window is non-empty
- [x] 5.3 Add a `netclaw-agent-memory` delta treating adopted context as ephemeral quoted material, not direct durable memory-write authority
- [x] 5.4 Ensure session semantics explicitly block unauthorized or pending content from originating tool calls, reminders, jobs, or direct durable memory writes

## 6. Spec Sync

- [x] 6.1 Rewrite `proposal.md`, `design.md`, and existing deltas to reflect the authorized-adoption MVP
- [x] 6.2 Add any missing capability deltas required to make the authority model complete
- [x] 6.3 Sync the completed change into main specs via `/opsx-sync`

## 7. Verification

- [x] 7.1 Verify the revised artifacts are internally consistent across ACL, adapters, session, slash commands, approvals, memory, and thread hydration, including explicit ownership boundaries
- [x] 7.2 Verify the MVP remains smallest-scope and secure-by-default, with deterministic retry/replay behavior and no ambiguous persistence ordering
