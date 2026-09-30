## Context

`tool-authorization` TA-3 states that grant categories are not an
authorization input. Two specs and the PRD still describe the old model.

## Goals / Non-Goals

**Goals:** make the text match the code. **Non-Goals:** add a creation-time
tool check for scheduled tasks.

## Decisions

- Keep the scenario name "Reject task with ungrantable tools" so that the
  requirement keeps all its scenarios, and rewrite its steps to the real
  behavior: the denial happens when the task runs.

## Risks / Trade-offs

- [The scenario name reads as a creation-time rejection] → The steps state
  the execution-time denial.
