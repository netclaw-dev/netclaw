## Context

The actor stores completed turns and tool work. It does not store a model only request before its acknowledgment. Its buffer is also actor local.

The reminder manager already persists schedules, routes a `current_session` turn, deduplicates delivery, and records the result.

## Goals

- Store accepted input before acknowledgment.
- Stop an eligible model call during graceful drain.
- Wake the session within ten minutes through the reminder manager.
- Restore pending input under its recorded authority.
- Add no channel adapter or second delivery path.

## Non Goals

- Resume after an ungraceful crash.
- Replay a turn after a tool starts or partial text reaches a user.
- Create a daemon restart command or a configuration property.

## Decisions

### D1. The session journal owns accepted input

`InputAdmitted` stores an `InputId`, content, media, source ID, executable text, and `TurnContextRecord`. The actor persists it before acknowledgment.

`TurnRecorded` and `ToolBatchStarted` close the input IDs that they consume. `InputClosed` closes input after a terminal path without either event.

`SessionState` keeps the ordered pending input ledger. A bounded source ID ledger rejects a retry after a lost acknowledgment.

### D2. Drain produces a standard reminder

The actor gives the model call a short completion grace. It cancels the call and waits for its task to stop when the grace ends.

The actor creates a standard one shot `ReminderDefinition` only when these conditions hold:

- pending input exists;
- no tool batch started;
- no partial text reached a subscriber;
- the stored turn context is valid;
- the stored turn has a channel type for current-session delivery.

The reminder expires ten minutes after the interruption. The restart manifest stores only restart reminder definitions.

### D3. The reminder manager owns wakeup and delivery

Startup registers each fresh definition through `SaveReminderCommand`. The reminder uses `DeliveryKind.CurrentSession` and the existing gateway path.

The daemon adds no route binder, channel state, retry loop, or resume candidate protocol. The reminder manager owns route resolution, persistence, delivery retries, and deduplication.

### D4. The reminder is a trigger

The reminder text is generic: `Resume the work that was interrupted by the daemon restart.`

When this internal reminder arrives, the actor restores its pending input and original `TurnContextRecord`. The model sees the stored input and the restart notice.

The reminder does not replace the original authority. A missing or incompatible context causes a visible operator warning and no model call.

## Ordered Flow

This flow is schematic. It omits persistence callbacks and reminder delivery acknowledgments.

```text
input -> session: SendUserMessage
session -> journal: InputAdmitted
journal -> session: stored
session -> source: CommandAck
stop -> session: PrepareForDaemonRestart
session -> model: cancel and await stop
session -> stop: DaemonRestartPrepared with a reminder or no reminder
stop -> manifest: restart reminders
start -> reminder manager: SaveReminderCommand
reminder manager -> existing gateway: current_session reminder
gateway -> session: SendUserMessage
session -> journal: restore pending input and authority
session -> model: resume prior work
```

## Risks

- A stale reminder can start old work. The one shot definition has an absolute expiration.
- A tool can have an uncertain effect. `ToolBatchStarted` closes its input before execution and blocks this path.
- A partial reply can repeat text. The actor records transient text emission and does not create a reminder.
- A reminder can register twice after a process failure. Its stored ID makes `CreateOnly` registration idempotent.
- A stored turn can lack a channel type. The actor then creates no reminder.
- The reminder system owns gateway resolution for a stored channel type.
