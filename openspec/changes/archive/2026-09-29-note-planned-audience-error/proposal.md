## Why

The owner decided on September 29 that a missing or unreadable audience must
fail loudly instead of falling back to `Public`. The code change comes in a
later PR. TA-1 must keep describing today's behavior, but a reader must see
that the fallback is planned to go.

Source PRDs: PRD-002 (SEC-003).

## What Changes

- Add a "Planned change" note to `tool-authorization` TA-1. The note says that
  a follow-up code PR makes a missing or unreadable audience an error, and that
  the `Public` fallback is current behavior until then.
- No behavior change. No code change.

In scope: the note. Out of scope: the code change and its tests.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-1 gains a planned-change note. Its rules and
  scenarios do not change.

## Impact

Spec text only. The architecture document lists the same item under "Future
scenarios" with the level "Planned".

### Security and operational impact

None now. The follow-up code PR narrows behavior: a call that falls back to
`Public` today will fail instead.
