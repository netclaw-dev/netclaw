## Context

See `proposal.md` for the reason. Today a gateway `current_session` run waits for a delivery result after `CommandAck`. Four binding actors report that result. The session also keeps an in-memory reminder ledger, an in-flight reservation, and three pre-checks.

The session already stores each admitted input in its journal, with `SourceMessageId = "{reminderId}:{fireTimeMs}"` and `SourceKind = "reminder"`. `SessionState.PendingInputs` lists inputs that no turn has consumed. `RecentSourceMessageKeys` (256 keys, in the snapshot) lists keys that the session admitted.

Schematic flow (omits the security gates):

```
Exec (gateway route)
  key = ReminderFireKey(defId, dueMs)
  Ask(gateway, DeliverTrustedSessionTurn{ReminderId = key}, 10 s)
  CommandAck            -> report ok   (rule: admission ends the run)
  CommandNack / timeout -> report failed

Session.SendUserMessage (every phase)
  restart-resume message? -> resume if Ready with pending input, else ack no-op
  decision = PrepareAdmission(cmd)        // pure, before any side effect
    Duplicate: stored key seen          -> ack
    Dropped:   fire, and a pending input of the same reminder is not selected -> ack, log
    Persist:   continue (Ready: plan abandonment, then admit)
```

## Goals / Non-Goals

**Goals:**

- Admission by the session ends a Mode B run.
- One durable owner decides duplicate and drop. It reads the journal state only.
- A duplicate, a dropped fire, and a restart-resume message cause no input and no side effect.

**Non-Goals:**

- The #2491 Reminder-origin restart route keeps its wait for the correlated terminal turn. Rule 3 applies to the gateway route only.
- Restart resume with pending inputs of mixed requesters. It stays as it is and needs a separate change.
- Background-job dedup (`ProcessedBackgroundJobIds`, job in-flight set). It has the same shape and stays for later work.
- A history note or status for a dropped fire. A log line is enough.
- No change to authorization contexts. `docs/architecture/tool-authorization.md` needs no update.

## Decisions

**Reminder-owned `ReminderFireKey`.** A small value type parses and formats the existing `"{id}:{ms}"` text. The reminder id is a slug without a colon, and the time is digits only. Alternative: a new `InputAdmitted` field. Rejected: it needs a journal change, and old rows would lack it. Text that does not parse is never dropped and never blocks, so the rule fails safe toward admission.

**Drop only while the earlier fire waits.** The drop rule reads `PendingInputs` and skips inputs that the running turn selected. The rule then yields at most one waiting fire per reminder and does not depend on when a running turn writes its first tool batch. The journal list makes the rule survive restart and passivation.

**Decide before any side effect.** In `Ready`, `HandleIncomingUserMessage` calls `PrepareAdmission` before it plans tool batch abandonment. A duplicate or dropped fire then leaves the session as it was. Alternative: keep the reminder pre-check first. Rejected: it needs the in-memory ledger that this change deletes.

**Restart-resume is not an input.** The restart reminder text and id prefix identify it. It never reaches `PrepareAdmission`. This replaces the in-flight reservation that kept a redelivery away from the input path.

**Keep `delivery_required`.** It still gates `channel` delivery (Mode A). For `current_session` it has no effect, and the tool text and the skill say so.

**Delete the observer chain completely.** `MessageSource.DeliveryObserver`, `ReminderDeliveryResult`, the engine reporters, and the unused engine constructor parameters (`channelType`, `channelName`, `readObservedAtMs`) go. `SignalRSessionActor` loses `_deliveredThisTurn`.

## Risks / Trade-offs

- [A failed Mode B post no longer retries at reminder level] -> The session-level delivery retry stays. A TUI reminder with no client runs and the output stays in the transcript. The owner accepted this.
- [A first restart-resume message that arrives while another turn runs is a no-op] -> The pending input waits for a later restart. Mixed-requester resume is a separate change.
- [A wrong drop key blocks later fires] -> Example specs, model-based specs, and a focused mutation target cover the decision.
- [Duplicate check bounded to 256 keys] -> Same bound as before for operator inputs. A redelivery older than 256 admissions is outside the Akka.Reminders retry window.

## Migration Plan

No journal, snapshot, or config change. An older binary reads every row. An old execution actor that waits for a delivery result dies with the daemon. Akka.Reminders redelivers after the 70-minute lease. The new binary sees a duplicate key and acknowledges it.
