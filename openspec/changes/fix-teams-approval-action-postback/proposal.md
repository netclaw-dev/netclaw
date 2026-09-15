## Why

Teams receives a valid `adaptiveCard/action` invoke for a fresh approval card.
The Teams binding rejects the action before the shared approval flow runs.
The current rejection code does not identify the failed validation stage.

## What Changes

- Add safe Teams-only reason codes for each approval postback validation failure.
- Trace the trusted action locator from the Teams SDK invoke to the persisted pending approval.
- Correct the proven locator or route boundary without weakening approval checks.
- Add Personal, Posts, Threads, expiry, replay, and rejection-matrix tests.
- Preserve the PR #46 SDK card serialization correction.

## Capabilities

### New Capabilities

- `teams-approval-action-postback`: A trusted Teams `Action.Execute` callback resolves its matching pending approval and returns the authoritative terminal card.

### Modified Capabilities

- None.

## Impact

Source PRDs: `PRD-001`, `PRD-002`, `PRD-006`, and `PRD-009`.

The change affects only Teams invoke translation, Teams conversation routing, and the Teams binding actor.
It does not change shared approval authority, MCP behavior, other channels, policies, runtime versions, or deployment.

Security impact: Teams keeps tenant, team, channel, root, requester, correlation, nonce, offered-option, and replay checks.
Diagnostics contain only a fixed rejection reason.

Operational impact: an operator can distinguish a malformed action from an invalid destination, locator, nonce, requester, option, or retry.
