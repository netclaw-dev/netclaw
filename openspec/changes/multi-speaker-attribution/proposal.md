## Why

Netclaw needs multi-speaker thread context without letting unauthorized speakers
drive execution. The current change introduces an `Observe` path that still
feeds unauthorized live messages into the ordinary turn stream. That is too
permissive for MVP: it blurs the line between quoted context and executable
instructions, and it leaves slash commands, approvals, jobs, reminders, and
memory writes under-specified.

The smallest secure MVP is stricter: only authorized users create executable
turns. Other thread messages remain pending context until an authorized user
speaks. At that point, Netclaw explicitly adopts the unsynced thread window as
quoted context, persists an audit record describing what was adopted and under
whose authority, feeds the model a canonical attribution projection derived from
that record, and executes only the current authorized message.

## What Changes

- Remove the proposed `Observe` ACL outcome and replace it with a stricter
  authorized-only execution model.
- Maintain a per-thread authorized sync watermark that marks the highest thread
  message included under an authorized turn.
- On each authorized threaded inbound message, hydrate unsynced thread messages
  before that message into an explicit adopted-context window instead of passing
  them as ordinary live turns.
- Persist an adopted-context audit record containing authorizer identity, sync
  bounds, included message ids and timestamps, canonical inclusion-time
  authority from the same live turn-creation authorization basis used at
  adoption time, and the materialized adopted window used for the model.
- Require approval prompts and stored approval context to identify the current
  authorizer and, when the adopted-context window is non-empty, to name the
  adopted speakers from that window by stable sender id.
- Feed the LLM a canonical attribution projection derived from the persisted
  adopted-context record, followed by the current authorized executable message.
- Make adoption ordering deterministic: persist the adopted-context record
  first, enqueue the authorized turn only after that persistence succeeds, and
  advance the authorized-sync watermark only after the threaded adapter receives
  session confirmation that enqueue succeeded.
- Use the current authorized message identity as the adoption idempotency basis.
  Replays or retries for the same authorized message SHALL reuse an existing
  adopted-context record instead of duplicating it.
- Reserve execution semantics for the current authorized message only. Pending
  or adopted unauthorized content cannot directly trigger model turns,
  slash-command dispatch, approvals, tool calls, reminders, jobs, or durable
  memory writes.
- Omit adopted-context persistence and adopted-context framing entirely when the
  authorized message has no unsynced gap before it.
- Define canonical framing and escaping for reserved adopted-context markers so
  user-supplied content cannot spoof Netclaw's authority markers.

## Capabilities

### Modified Capabilities

- `netclaw-acl`: clarify that only authorized users create executable inbound
  turns; unauthorized live thread messages are not forwarded as turns.
- `netclaw-input-adapters`: build authorized-turn envelopes that combine an
  adopted-context projection with the current authorized executable message.
- `thread-history-backfill`: replace live-history merge semantics with
  authorized-sync watermarking and adopted-context window hydration.

### Added Capability Deltas In This Change

- `netclaw-session`: persist adopted-context audit records and treat adopted
  context as quoted non-executable input.
- `slash-command-dispatch`: only current authorized executable messages may
  enter slash-command interception.
- `tool-approval-gates`: approval requests may originate only from tools caused
  by the current authorized executable message.
- `netclaw-agent-memory`: adopted context is ephemeral quoted material, not
  authoritative conversation history for direct durable memory writes unless the
  authorized message explicitly elevates it.

## Impact

- **ACL layer**: no `Observe` outcome. Channel ACL remains an executable-turn
  gate for live inbound messages.
- **Thread adapters**: own source-thread gap fetch and watermark bookkeeping,
  fetch unsynced thread history only when an authorized message arrives, and
  advance the watermark only after session-confirmed enqueue success.
- **Turn construction**: threaded authorized messages are framed as one adopted
  context window plus one executable authorized message.
- **Session persistence**: own adopted-context persistence and execution
  linkage, reuse existing adopted-context records for same-message retries, and
  keep deterministic persist, enqueue, and watermark sequencing.
- **Prompt semantics**: adopted context is explicit quoted context, not ordinary
  authoritative turn history.
- **Control surfaces**: slash commands, approvals, jobs, reminders, tool calls,
  and durable memory writes align to the authorized executable turn only, and
  approval artifacts carry authorizer plus adopted-speaker provenance.
