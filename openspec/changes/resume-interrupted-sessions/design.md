## Context

See [proposal.md](proposal.md) for the user outcome. The actor currently acknowledges a model-only request and each buffered request before a journal event stores that input. `TurnRecorded` stores one user message after the reply. A stop clears the actor-local queue. `RestartRecoveryService` warms prior sessions but does not start a model call. The manifest exists only for a configuration restart.

The [engineering glossary](../../../docs/spec/GLOSSARY.md) defines shared session terms. `TurnContextRecord` already stores the authority fields that a recovered tool approval uses. The channel bindings own output subscribers. A warm session alone cannot deliver a recovered reply to Slack, Discord, or Mattermost.

## Goals / Non-Goals

**Goals:**

- A journal record precedes each accepted input acknowledgment.
- The journal and snapshots retain accepted input until a durable terminal event consumes it.
- A graceful stop confirms a model task stop before it grants a resume candidate.
- A short restart resumes only eligible work under its original authority.
- One model call receives the ordered accepted queue after the original turn ends.

**Non-Goals:**

- A crash does not create an automatic resume candidate.
- A tool with an uncertain effect does not run again without new user input.
- A new reminder definition does not represent interrupted work.
- A channel without a confirmed output route does not get an automatic model call.

## Decisions

### D1. The journal owns admitted input

Add `InputAdmitted` with an input ID, a stable source message ID when one exists, text, media, executable text, source IDs, received time, and a `TurnContextRecord`. The actor persists it before the ack. The record excludes actor refs and raw `MessageSource`. Reject a missing or invalid authority record before the model call. The actor uses a source ID only within its channel and session scope for deduplication. Sources without a stable ID cannot claim retry deduplication.

The actor keeps admitted records in an ordered state list. `TurnRecorded` identifies every input in its model call. `ToolBatchStarted` identifies the inputs before any tool runs. A durable terminal failure event consumes a model-only input. This prevents a failed turn from starting again after a short stop. The live callback and journal replay use the same input IDs. The snapshot stores pending records and a bounded recent source-ID ledger. The actor skips a snapshot while a pending input also appears only in transient history.

The old `TurnRecorded.UserMessage` remains for journal compatibility. Replay uses admitted records when the new consumed-ID list exists. Replay uses the old field for earlier journal records. This keeps old sessions readable.

Alternative: Store the input only in a stop manifest. That misses a stop after an ack and before manifest creation. It also duplicates session authority outside the journal.

### D2. Drain returns a classified result

`PrepareForDaemonRestart` asks the actor to stop new work. A live model call gets a short grace. If it completes, the actor handles its normal result. If it remains active, the actor cancels its token and awaits the exact `SessionLlmInvoker` task. A call ID rejects stale task results. The actor grants a candidate only when the task stopped, no tool batch started for that turn, and no user-visible text escaped. A confirmed queue can form a candidate after a completed reply. Approval-only work follows the existing durable approval path without a candidate.

The actor returns a typed drain result with the candidate input IDs or a blocked reason. `SessionDrainHelper` collects results. The daemon writes one manifest after all drain replies. Both the config restart and normal coordinated stop use this path. The manifest write is atomic. It stores an absolute ten-minute deadline per candidate. A timeout or failure produces a warning and no executable candidate.

Alternative: Infer candidates from actor phase or session IDs. Phase does not prove task cancellation, and a warm session can contain a completed turn.

### D3. Recovery checks the manifest and the session journal

The recovery service reconciles the session catalog and warms listed sessions. After channel services start, it prepares the output route for a candidate. It then sends an internal resume request with the candidate IDs and absolute deadline. The actor checks the deadline again. It checks that the IDs still match its pending journal records, no newer turn started, and the recorded authority is valid. The actor resumes the original turn first. It then sends one ordered follow-up model call for accepted queued input.

The service uses existing channel gateway `StartProactiveThread` messages to rebuild Slack, Discord, and Mattermost bindings. Those messages must confirm the output subscriber before the resume request. A TUI or SignalR session needs a live attachment; otherwise the candidate stays blocked and the service reports it. The service must retain a candidate until it succeeds or expires. It does not reset the deadline after another process start.

Alternative: Schedule a generic reminder. That creates a new turn, can abandon a parked approval, and may run under authority derived from the reminder rather than the original input.

### D4. Authority and output safety gate

Each admitted record carries its original `TurnContextRecord`. The actor reconstructs it with `TurnContext.TryFromRecord`. It never derives a recovery authority from a session ID. A queue with incompatible boundaries remains durable but does not auto-resume. A queue with compatible authority uses the narrowest audience and never widens tool access. The actor rejects a candidate after partial text output because the user might have seen that text. It also rejects a candidate after a tool batch starts because a tool might have an external effect.

Positive example: A Slack user sends one request. The model call stops before text or tools. The next start restores the Slack binding and resumes that input under its stored personal boundary.

Negative example: A shell tool starts before a stop. The daemon reports the pending work and does not replay the shell call.

### D5. Delivery order and failure behavior

The candidate deadline starts at interruption. The recovery service checks it before each route attempt. The actor checks it before the model call. An expired candidate causes one warning and leaves the journal record available for forensic inspection or a new user turn. A newer input supersedes an old candidate. The manifest remains on disk until each candidate reaches a terminal recovery decision. The service records a blocked route or invalid record with a clear diagnostic.

## Ordered flow

This diagram is schematic. It omits the channel ACL and persistence callbacks.

```text
source -> session: SendUserMessage
session -> journal: InputAdmitted(input ID, authority, content)
journal -> session: persisted
session -> source: CommandAck
session -> model: original turn
stop -> session: PrepareForDaemonRestart
session -> model: cancel if grace ends
model -> session: task stopped
session -> stop: candidate(input IDs) or blocked reason
stop -> manifest: atomic write(deadline)
start -> channel: restore output binding
channel -> start: binding ready
start -> session: ResumeInterruptedTurn(input IDs, deadline)
session -> journal: validate pending input and authority
session -> model: original turn, then one queued call
```

## Risks / Trade-offs

- [Input accepted during a write failure] → The actor sends a nack and starts no model call.
- [Snapshot skips an unconsumed input] → Replay starts before that snapshot, then restores the pending record.
- [Provider ignores cancellation] → Drain reaches its existing deadline and records no candidate.
- [A reply reaches the user before cancellation] → The actor reports a blocked candidate and avoids duplicate text.
- [A tool starts before cancellation] → The actor reports a blocked candidate and avoids duplicate effects.
- [A route is absent after restart] → The service reports the candidate and leaves the agent quiet.
- [Old manifest or a second process start] → The absolute deadline and input IDs prevent a new or duplicate model call.
- [A source has no stable message ID] → The actor records a unique admission ID but cannot deduplicate a source retry.

## Migration Plan

1. Add the journal and snapshot fields with new protobuf tags and a new manifest for `InputAdmitted`.
2. Keep old event fields and old journal readers intact.
3. Add actor admission and recovery tests before enabling automatic wakeup.
4. Add the normal stop manifest and recovery request after the actor contract passes.
5. Verify a graceful stop and a short restart in an isolated daemon with a persistent home.
6. Roll back by disabling automatic wakeup in the new binary. Keep the new journal decoder so accepted input remains readable.
