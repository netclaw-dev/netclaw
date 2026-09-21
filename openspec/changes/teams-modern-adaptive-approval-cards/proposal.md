## Why

Teams approval cards use a basic layout that does not show each approval state clearly. The supplied card designs define a clearer security-control presentation.

## What Changes

- Add the modern Teams approval-card presentation for pending, granted, denied, expired, and neutral states.
- Target Adaptive Cards schema 1.5 for all Teams card payloads.
- Preserve `Action.Execute`, the `netclaw-approval` verb, and the existing opaque callback data names.
- Render only session-supplied approval options in the supplied order.
- Keep expiry as a Teams presentation event that creates a replacement card without a session decision.
- Keep the elevated card as a tested presentation variant. Teams does not select it without a canonical risk signal.
- Keep the current display-name fallback until the authenticated SDK boundary exposes a safe display-name value.

## Capabilities

### New Capabilities

- `teams-approval-presentation`: Teams Adaptive Card visual presentation for session-owned tool approvals.

### Modified Capabilities

- None.

## Impact

Source PRDs: `PRD-001`, `PRD-002`, `PRD-006`, and `PRD-009`.

This change affects the Teams channel, the Teams daemon transport edge, Teams tests, and Teams documentation. It does not change an API, package, tenant setting, tool policy, grant store, or another channel.

Security impact: the session actor remains the approval authority. The Teams card remains an opaque callback transport with existing sender, tenant, correlation, nonce, expiry, option, and replay checks.

Operational impact: card expiry keeps the session request pending. Teams sends a fresh card with a fresh nonce. No deployment or Teams configuration change is required.
