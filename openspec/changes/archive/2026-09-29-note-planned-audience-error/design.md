## Context

TA-1 states that some components fall back to `Public` when an audience is
missing or cannot be parsed. Tests pin that fallback. The owner accepted a
stricter rule for a later code PR.

## Goals / Non-Goals

**Goals:** make the planned change visible next to the current rule.

**Non-Goals:** change the rule now, or describe the planned rule as current.

## Decisions

- Put the note inside TA-1, not in a new requirement, so that no scenario
  describes behavior that the code does not have.
- The follow-up code PR changes TA-1 itself and removes the note.

## Risks / Trade-offs

- [A reader treats the note as current behavior] → The note says "Planned
  change" and "until that PR merges".
