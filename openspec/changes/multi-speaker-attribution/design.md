## Context

Netclaw's threaded adapters already need to hydrate prior thread messages when a
bot is mentioned mid-thread. The open problem is authority, not retrieval: the
system must let an authorized operator ask Netclaw to read the thread without
letting every other speaker become an executable instruction source.

The previous draft introduced an `Observe` ACL outcome and pushed unauthorized
messages through the ordinary inbound path with role labels. That is not the
smallest secure MVP. It still treats unauthorized live input as turn material,
creates more ACL states than we need, and leaves too much ambiguity around what
can trigger slash commands, approvals, jobs, reminders, and memory writes.

The revised model is stricter:

- only authorized users create executable turns;
- unauthorized or otherwise pending thread messages remain off the live turn
  path;
- when an authorized user speaks, the adapter hydrates the unsynced thread gap
  into an explicit adopted-context window;
- the adapter may construct the canonical attribution projection, but the system
  persists that exact projection plus adopted metadata before execution
  continues;
- only the current authorized message is executable.

## Goals / Non-Goals

**Goals:**

- Preserve multi-speaker thread context for authorized users.
- Keep execution authority fail-closed: only an authorized current message is
  executable.
- Persist enough adopted-context metadata for audit, replay, and later policy
  review.
- Make the LLM's framing unambiguous through canonical markers and escaping.
- Keep scope to the smallest secure MVP without inventing a generalized
  per-message authority engine.

**Non-Goals:**

- Display-name resolution beyond stable platform ids.
- Retroactive reclassification when ACL membership changes after adoption; the
  MVP captures authority at inclusion time and preserves it.
- Generalized support for multi-speaker authority on non-threaded transports.
- Fine-grained partial execution inside adopted context; the window is entirely
  quoted context in MVP.

## Decisions

### D1: Remove `Observe`; keep ACL binary for executable turns

**Decision**: Do not add a third ACL outcome.

**Rationale**: The secure boundary is "who may create an executable turn," not
"who may be visible somewhere in the thread." Unauthorized speakers do not need
their own live inbound outcome because their messages can be recovered from the
thread source when an authorized user later adopts the window. Keeping ACL
binary reduces ambiguity and avoids accidental downstream treatment of
unauthorized content as an ordinary turn.

### D2: Use a per-thread authorized sync watermark

**Decision**: Maintain a durable per-thread watermark representing the highest
thread ordering key whose authorized turn completed durably, plus a pending
cursor for the highest authorized inbound accepted for enqueue but not yet
durably completed.

**Rationale**: This gives the threaded adapter a minimal, replay-safe way to
compute the unsynced gap while staying fail-closed across crashes. Unauthorized
live messages do not need to be persisted separately in the adapter. They
remain pending in the source thread until the next authorized message adopts
them. A crash after enqueue acceptance but before durable turn completion does
not promote the durable watermark, so recovery cannot skip pending messages.

**Watermark semantics:**

- watermark is exclusive lower bound for the next adoption window;
- current authorized message is the exclusive upper bound of the adopted window;
- the threaded adapter owns source-thread gap fetch and watermark bookkeeping;
- adopted-context persistence success is a prerequisite for enqueue;
- after enqueue acceptance, the adapter may persist a pending cursor for the
  current authorized message;
- the adapter advances the durable watermark only after `TurnCompleted` or
  other durable turn completion for the current authorized message;
- if adopted-context persistence fails, the turn is not enqueued and the
  pending cursor and durable watermark do not advance;
- if enqueue fails after adoption persistence succeeds, the pending cursor and
  durable watermark do not advance and the persisted adopted-context record
  remains a non-executed audit artifact rather than proof of execution;
- if enqueue succeeds but durable turn completion never occurs, the durable
  watermark does not advance and recovery reuses the persisted adopted-context
  record for the same authorized message id;
- when no unsynced gap exists, no adopted-context record or projection is
  created and the authorized message is sent as an ordinary authorized turn.

### D3: Persist an adopted-context audit record before model execution

**Decision**: The session owns adopted-context persistence. It persists at most
one adopted-context record per authorized message identity when that message
includes unsynced thread material, and that record stores the exact canonical
projection used for execution.

**Record fields (MVP minimum):**

- session/thread id
- authorizer identity for the current authorized message
- sync lower bound and upper bound
- included message ids and timestamps
- included message sender ids
- authority-at-inclusion for each included message
- the exact canonical attribution projection actually fed to the model
- enough linkage to correlate retries or recovery for the same authorized
  message id

**Idempotency basis:** `(session/thread id, current authorized message id)`.
Retries, replays, or recovery for the same authorized message SHALL reuse the
existing adopted-context record and its exact persisted projection rather than
creating a duplicate or re-deriving a different projection from raw thread
history.

**Rationale**: The audit record is the authoritative source for what context was
adopted, who authorized that adoption, and exactly how the model saw it. This
is enough for retry, recovery, replay, incident review, and future UI work
without persisting raw live unauthorized turns as if they were ordinary
conversation history.

### D4: Adopted context is explicit quoted context, not executable turn history

**Decision**: The model sees adopted thread material inside a dedicated adopted
context window, followed by a separate current authorized executable message.

**Rationale**: This preserves the useful thread context while making the trust
boundary visible both to humans and to the model. It also gives downstream
surfaces a stable rule: only the current authorized message can trigger actions.

### D5: Canonical framing is exact-persisted and escape-safe

**Decision**: Define one canonical text projection for the model. The adapter
may construct that projection before handoff, and the session SHALL persist that
exact projection before execution continues.

**Canonical framing:**

```text
[adopted-context]
[adopted-message id={messageId} author={senderId} authority-at-inclusion={authorized|pending} ts={timestamp}]
{escaped message text}
[/adopted-message]
...
[/adopted-context]
[current-authorized-message author={senderId} ts={timestamp}]
{escaped current authorized text}
[/current-authorized-message]
```

**Escaping rule:** any user-originated line that begins with a reserved marker
prefix (`[adopted-context]`, `[/adopted-context]`, `[adopted-message `,
`[/adopted-message]`, `[current-authorized-message `,
`[/current-authorized-message]`) SHALL be escaped by prefixing that line with a
backslash in the projection. The persisted record SHALL store the escaped
projection that was presented to the model.

**Rationale**: Marker-level escaping is the smallest deterministic defense
against content spoofing. It avoids introducing a full encoding layer while
making the executed projection and the persisted audit record match exactly.

### D6: Inclusion-time authority is captured, not recomputed later

**Decision**: Each adopted message records authority-at-inclusion as either
`authorized` or `pending`, based on the same live turn-creation authorization
basis that governs the current inbound message at the moment of adoption.

**Rationale**: This preserves the actual decision environment for audit. Later
ACL changes do not mutate the historical record or the attribution projection
that the model saw.

### D7: Control surfaces key off the executable message only

**Decision**: Slash-command dispatch, model turn execution, tool approvals,
reminders, jobs, tool calls, and direct durable memory writes are all scoped to
the current authorized message.

**Rationale**: This keeps the authority model simple and complete. Adopted
context can influence interpretation, but it cannot directly originate a control
flow.

### D8: Approval artifacts carry authorizer and adopted-speaker provenance

**Decision**: Approval prompts and stored approval context SHALL identify the
current authorizer for the executable message. When the adopted-context window
for that turn is non-empty, those artifacts SHALL also indicate that adopted
context was present for the turn and name the adopted speakers by stable sender
id.

**Rationale**: Approval is a security boundary. The approving human needs to see
whose authority is executing the request and whether the request depends on
quoted context from other speakers.

## Flow

1. A threaded inbound message arrives.
2. ACL checks whether the sender is authorized to create an executable turn.
3. If unauthorized:
   - the adapter does not dispatch a model turn;
   - no slash-command interception runs;
   - no approval, reminder, job, tool call, or direct durable memory write path
     begins;
   - the message remains pending thread context in the source thread.
4. If authorized:
    - the threaded adapter loads the authorized sync watermark for the thread;
    - it fetches unsynced thread messages between watermark and current message;
    - it classifies each included message with authority-at-inclusion using the
      same live turn-creation authorization basis applied to the inbound event;
    - if the fetched gap is empty, it sends the current authorized message as an
      ordinary authorized turn, persists a pending cursor only after enqueue
      acceptance, and advances the durable watermark only after `TurnCompleted`
      or other durable turn completion;
    - otherwise it builds the canonical attribution projection;
    - the session persists or reuses the adopted-context record keyed by the
      current authorized message identity, including that exact projection and
      adopted message metadata;
    - if that persistence step fails, processing stops without enqueue or
      pending-cursor or durable-watermark advancement;
    - it enqueues one authorized executable turn consisting of adopted context
      plus current authorized message;
    - after enqueue acceptance, the adapter persists a pending cursor for the
      current authorized message;
    - if enqueue fails, the pending cursor and durable watermark remain
      unchanged and the persisted adopted-context record remains audit-only;
    - if durable turn completion never occurs, the durable watermark remains
      unchanged and retries or recovery reuse the persisted record for that
      authorized message id;
    - after `TurnCompleted` or other durable turn completion, the adapter
      advances the durable watermark to the current authorized message.

## Risks / Trade-offs

- **Token cost increase**: explicit adopted-context framing adds overhead.
  Acceptable for MVP because it buys a clear authority boundary.
- **Authorized adoption can still carry malicious quoted text**: true, but this
  is an intentional delegated-read act by an authorized user. Prompt-injection
  scanning still applies to hydrated messages.
- **No retroactive inclusion from deleted source messages after the fact**: if a
  platform message disappears before an authorized adoption, it cannot be
  included. Acceptable for MVP.
- **Quoted-context persistence adds schema work**: yes, but the audit value is
  worth the additive persistence cost and is smaller than persisting all pending
  live unauthorized turns as ordinary history.
