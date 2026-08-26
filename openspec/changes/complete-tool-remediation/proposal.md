## Why

PRD-006 requires tools that are simple, secure, and reliable for agents.
Current receipts store remediation text as an unchecked string, but no shared consumer uses the code.
Parent and child execution paths therefore depend on separate handwritten result text.

## What Changes

- Replace free-form remediation strings with a closed next-action code set.
- Require each recoverable correction to select one known remediation value.
- Use one shared presenter for parent and child model instructions.
- Keep remediation separate from tool authority and successful output continuation.
- Add regression cases from live behavior.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-tools`: Define a closed next-action code and one shared instruction path.

## Impact

The change affects internal tool receipts, parent and child result presentation, and tests.
The change adds no public API, durable receipt, configuration property, or approval authority.
Native-tool exposure and shell affordance corrections remain a separate stacked change.
