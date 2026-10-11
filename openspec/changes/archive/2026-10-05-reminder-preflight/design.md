## Context

See proposal.md for the motivation. The scheduled run (Mode A) starts a new
session through `ReminderExecutionActor`. That session has no interactive
approval. A call that needs approval reads stored grants. With no covering
grant, the call is denied. A grant is keyed by the turn audience
(`PersistApprovalCandidatesAsync` saves at `TurnContext.Audience`).

## Goals / Non-Goals

**Goals:**
- One code path for the chat and the CLI: the skill and the tool.
- The test uses the reminder's real authority.

**Non-Goals:**
- A separate unattended manual run (the approach of #2239 and #2241).
- A special approval mode, changed prompt options, or a history record.

## Decisions

- **The tool returns the prompt; the chat runs it.** The agent carries out the
  prompt in the current attended turn. Alternative: deliver the prompt as a new
  turn at the reminder audience (Mode B style). That needs session-actor
  changes so that buffered input does not merge into the running turn. Rejected
  for this change.
- **Equal audience.** Each turn's tool exposure and grants follow the turn
  audience. If the chat audience is wider, the test passes calls that the
  scheduled run denies. The tool refuses that case. The manager already hides
  a reminder above the caller audience.
- **One prompt builder.** `ReminderExecutionActor.BuildPrompt` serves both the
  scheduled run and the tool, so the text is the same.
- **CLI reuses the chat page.** `netclaw reminder run <id>` sets
  `ChatNavigationState.InitialMessage`, the mechanism that the init wizard uses.
  No new daemon endpoint.
- **Initiator-only answers need no new code.** The session accepts an approval
  answer only from the turn's requester sender.

Actor boundaries: the tool asks `ReminderManagerActor` with the existing
`GetReminderCommand` (call-local). No new actor message. No new durable state.

## Risks / Trade-offs

- [The test does real work and can post to the reminder channel] → The skill
  warns before the run.
- [The model can choose other commands on a later run, so a grant may not
  match] → The skill summarizes which steps have "Always" grants. Repository
  and "anywhere" grants accept more change.
- [A CLI chat is always Personal, so the CLI can test only Personal reminders]
  → The refusal names the audience of the chat that can run the test.
- [Failure: the reminder manager does not reply] → The ask times out and the
  tool call fails. No state changes.
