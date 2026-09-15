## Why

Teams rejects approval cards after the modern presentation change.
The current tests do not exercise the native Teams SDK activity serialization path.

## What Changes

- Build a native Teams message activity for each approval card.
- Serialize the full activity before the SDK sends or updates it.
- Use the card schema wire values that the Teams client accepts.
- Omit text for card-only approval messages when the SDK permits it.
- Add Teams-only safe diagnostics for payload, activity, and SDK delivery stages.
- Add tests that use realistic approval requests and actual SDK activity serialization.

## Capabilities

### New Capabilities

- `teams-adaptive-card-live-delivery`: Teams approval cards use a serializable native SDK activity and safe delivery diagnostics.

### Modified Capabilities

- None.

## Impact

Source PRDs: `PRD-001`, `PRD-002`, `PRD-006`, and `PRD-009`.

The change affects the Teams reply client and its Teams-only tests.
It does not alter approval authority, callback data, other channels, or deployment settings.

Security impact: diagnostics use fixed stage names and exception types only.
They do not log identifiers, text, card data, secrets, or response bodies.

Operational impact: operators can locate a card build, serialization, create, reply, or update failure.
