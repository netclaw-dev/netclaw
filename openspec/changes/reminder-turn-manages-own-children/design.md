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
    if requester is a reminder turn:
        return requester.session  == owner.session
           and requester.audience == owner.audience
           and requester.boundary == owner.boundary
           and requester.channel  == owner.channel
    return requester.HasSameRequester(owner)

requester is a reminder turn:
    requester.principal == VerifiedAutomation and requester.sourceKind == "reminder"
```

The pseudocode is schematic. It omits the legacy-run branch of `ControlChild`. That branch uses the same comparison.

The relaxation looks only at the requester. The owner of the run is not tested for being a reminder. Thus:

| Run owner | Requester | Result |
|-----------|-----------|--------|
| person (`local`, Operator) | reminder, same session | allowed (new) |
| person | reminder, other session | denied |
| person | reminder, other audience, boundary, or channel type | denied |
| person Alice | person Bob, same session | denied (unchanged) |
| reminder | reminder, same session | allowed (the six facts match; unchanged) |
| reminder | person, same session | denied (unchanged) |
| person | `VerifiedAutomation` with another source kind | denied (unchanged) |

Alternatives:

- Change `HasSameRequester`. Rejected. Input batching uses it. A reminder input would join a person's turn, and D1 and D3 would break.
- Give the reminder the sender of its creator (investigation Option A). Rejected. The definition stores no creator. The change needs a durable record change and a delegated grant scope.
- Keep the strict rule and add error text (investigation Option B). Rejected by the owner: a person who may start a reminder may manage a sub-agent.
- Allow status only (investigation Option C). Rejected by the owner.

### D2. The reverse direction does not change

A person cannot manage a run that a reminder turn started. The owner decision covers the reminder side only. The rule stays symmetric in one respect: the run owner is not a reminder-specific concept. The follow-up question for the owner is in the pull request description.

### D3. Identification of a reminder turn

The check reads two recorded facts of the request context: the principal classification and the source kind. Both come from the `MessageSource` that `ReminderExecutionActor` builds. A channel binding copies them into the turn context. A person's message cannot set the source kind. The check needs both facts, so a webhook turn (`VerifiedAutomation`, other source kind) gets no relaxation.

### D4. Focused mutation target

The comparison is a security boundary, so it gets a narrow Stryker target in `TOOLING.md`, as `HasSameRequester` has. The linked `TurnContextChildControlTests` file kills the mutants.

## Risks / Trade-offs

- [In a shared channel session, a reminder that one participant wrote can manage a child run that another participant started] -> Accepted by the owner decision. The session is the trust unit for a reminder. The reminder still needs the stored audience and boundary to match. Any participant who can set a reminder in that session can already ask the agent to act in it. The architecture document states this limit.
- [A stored reminder prompt can carry injected text, and it can now cancel a run of the person] -> Same decision. The cancel only acts on a run of its own session. A cancel cannot grant any tool authority. A child result never grants authority.
- [All reminders of a session share one sender ID] -> Unchanged. A later change can give each reminder its own sender ID. It does not affect this rule, because the comparison ignores the sender.
- [A restart-resume reminder adopts the authority of the interrupted input] -> That turn is not a reminder turn and uses the six-fact rule with the recorded sender. No change.

## Migration Plan

No data migration. Old runs keep their `OriginalContext`. The new check reads only fields that the record has held since #2467.
