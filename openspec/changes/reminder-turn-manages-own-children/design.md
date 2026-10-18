## Context

See `proposal.md` for the reason. The terms [requester](../../../docs/spec/GLOSSARY.md#requester) and the child ownership rule come from `docs/architecture/tool-authorization.md` ("Child ownership").

Current state on `dev`:

- `check_agent_run` returns a request. The tool pipeline adds the `TurnContext` of the current turn. `LlmSessionActor.ControlChild` compares it with the durable `OriginalContext` of the run.
- `ControlChild` calls `HasSameChildRequester`, which calls `TurnContext.HasSameRequester`. The check compares six facts: session, audience, boundary, channel type, sender ID, principal classification.
- A reminder fire of delivery kind `current_session` has sender `reminder-system`, principal `VerifiedAutomation`, and source kind `reminder`. It keeps the stored audience, the stored boundary, and the stored origin channel type.
- `SessionInputFlow` uses `HasSameRequester` for a second purpose: it decides which buffered inputs join one turn. That use must not change. A reminder input never joins the turn of a person (decision D1 of `d1-one-authority-per-turn`).

## Goals / Non-Goals

**Goals:**

- A reminder turn manages the child runs of its own session.
- Every other requester keeps the current rule.
- One narrow code change, with one owner and no new data.

**Non-Goals:**

- No change to who may answer an approval prompt. A reminder turn keeps `VerifiedAutomation` for consent.
- No change to input batching or to approval abandonment (D1, D3).
- No reminder creator record. `ReminderDefinition` still stores no sender and no principal.
- No per-reminder sender ID. All reminders of a session still share `reminder-system`.
- No change to the error text.

## Decisions

### D1. A new comparison for child control; `HasSameRequester` stays

Owner of the decision: `LlmSessionActor.ControlChild`.
Data: the `OriginalContext` of the run is durable (session journal). The request context is call-local.

```text
schematic
CanControlChildRunOf(requester, owner):
    if requester is a reminder turn or owner is a reminder turn:
        return requester.session  == owner.session
           and requester.audience == owner.audience
           and requester.boundary == owner.boundary
           and requester.channel  == owner.channel
    return requester.HasSameRequester(owner)

X is a reminder turn:
    X.principal == VerifiedAutomation and X.sourceKind == "reminder"
                and X.transportAuthenticity == LocalProcess
```

The pseudocode is schematic. It omits the legacy-run branch of `ControlChild`. That branch uses the same comparison.

The relaxation applies when one side is a reminder turn. The other side can be any turn of the session: a person turn, or an automated turn such as the delivery turn of a background job. The rule names the reminder turn, not the person. The requester is the turn that asks. The owner is the recorded `OriginalContext` of the run. Thus:

| Run started by | Requester | Result |
|----------------|-----------|--------|
| person (`local`, Operator) | reminder, same session | allowed (new) |
| reminder | person, same session, same audience, boundary, and channel type | allowed (new, owner answer 18 October) |
| reminder | background-job delivery turn, same session, same audience, boundary, and channel type | allowed (new; any turn of the session) |
| background-job delivery turn | reminder, same session, same audience, boundary, and channel type | allowed (new; any turn of the session) |
| reminder | another person (Bob), same session | allowed (new, accepted shared-channel effect) |
| reminder | reminder, same session | allowed (the six facts match; unchanged) |
| person | reminder, other session | denied |
| reminder | person, other session | denied |
| person | reminder, other audience, boundary, or channel type | denied |
| reminder | person, other audience, boundary, or channel type | denied |
| person Alice | person Bob, same session | denied (unchanged) |
| person | `VerifiedAutomation` with another source kind | denied (unchanged) |
| `VerifiedAutomation` with another source kind | person of another sender | denied (unchanged) |
| webhook turn with source kind `reminder` (`Verified`) | person, same session | denied (a webhook is not a reminder turn) |
| person | webhook turn with source kind `reminder` | denied |

Alternatives:

- Change `HasSameRequester`. Rejected. Input batching uses it. A reminder input would join a person's turn, and D1 and D3 would break.
- Give the reminder the sender of its creator (investigation Option A). Rejected. The definition stores no creator. The change needs a durable record change and a delegated grant scope.
- Keep the strict rule and add error text (investigation Option B). Rejected by the owner: a person who may start a reminder may manage a sub-agent.
- Allow status only (investigation Option C). Rejected by the owner.

### D2. The relaxation is symmetric

The owner answered on 18 October 2026: a person turn of the same session may check and cancel a child run that a reminder turn started. The check therefore tests both sides for a reminder turn. A turn that is not the sender of the run's starter gets no more than the session, audience, boundary, and channel type check, and only when a reminder turn is on the other side. Alternative: relax the requester side only (the first version of this change). Rejected by the owner.

### D3. Identification of a reminder turn

The check reads three recorded facts of each turn context: the principal classification, the source kind, and the transport authenticity. All three come from the `MessageSource` that `ReminderExecutionActor` builds, and a channel binding copies them into the turn context. A person's message cannot set them.

The source kind alone is not enough. `WebhookExecutionActor` gives a webhook turn the principal `VerifiedAutomation`. It takes the source kind from the event header or the route name, and the authenticated webhook sender controls both. So a webhook turn can carry the source kind `reminder`. The webhook path sets the authenticity `Verified` in code. The reminder path sets `LocalProcess` in code, in both of its delivery paths. The check requires `LocalProcess`, so a webhook turn gets no relaxation, as the requester or as the owner. The sender ID `reminder-system` would also separate the two, because the webhook sender is `webhook:<route>`. The authenticity is the better fact: it is a typed value that code sets, and it does not depend on a string. The background-job delivery turn also has `LocalProcess`. Its source kind `background-job` keeps it apart.

A recorded turn context that lacks the source kind is not a reminder turn. This fails closed.

### D4. Focused mutation target

The comparison is a security boundary, so it gets a narrow Stryker target in `TOOLING.md`, as `HasSameRequester` has. The linked `TurnContextChildControlTests` file kills the mutants.

## Risks / Trade-offs

- [In a shared channel session, a reminder that one participant wrote can manage a child run that another participant started, and any participant can manage a child run that a reminder started] -> Accepted by the owner on 18 October 2026. The session is the trust unit for a reminder. The audience, boundary, and channel type must still match. The architecture document states this limit.
- [A stored reminder prompt can carry injected text, and it can now cancel a run of the person] -> Same decision. A cancel acts only on a run of its own session. A cancel grants no tool authority. A child result never grants authority.
- [All reminders of a session share one sender ID] -> Unchanged. A later change can give each reminder its own sender ID. It does not affect this rule, because the comparison ignores the sender.
- [A restart-resume reminder adopts the authority of the interrupted input] -> That turn is not a reminder turn and uses the six-fact rule with the recorded sender. No change.

## Migration Plan

No data migration. Old runs keep their `OriginalContext`. The new check reads only fields that the record has held since #2467.
