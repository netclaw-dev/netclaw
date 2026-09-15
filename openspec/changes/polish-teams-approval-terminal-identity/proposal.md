## Why

Teams approval terminal cards identify every operator as "Authorized operator".
This hides useful human context after a live approval decision while the card
must remain clear that approval is not tool execution.

## What Changes

- Show a bounded, sanitized Teams-provided display label on granted and denied
  terminal cards when one is available; otherwise keep the exact fallback
  "Authorized operator".
- Preserve the existing canonical sender identity (`AadObjectId`, then `Id`)
  for every authorization, routing, persistence, and telemetry decision.
- Change the granted-card execution field to `Execution State: Execution
  Approved`; terminal text continues to avoid a claim that the tool ran or
  completed.
- Keep the current denial, approval, expiry replacement, and replay behavior.

## Capabilities

### New Capabilities

- `teams-interactive-approval`: Render and route Teams interactive approval
  terminals with presentation-only operator identity and accurate execution
  state.

### Modified Capabilities

- None.

## Impact

The Teams SDK translator, SDK-free approval action contract, personal and
channel routing actor, and terminal-card renderer change. No Microsoft Graph
calls, UPN lookup, group configuration, persistence schema, callback data, or
authorization rule changes are introduced. The display value is bounded and
sanitized at the transport edge, is not logged, and is not stored with the
canonical security identifier.
