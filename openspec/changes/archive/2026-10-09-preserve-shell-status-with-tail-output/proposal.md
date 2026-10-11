## Why

Tail-only tool results reduce the context that models must interpret.
The new window also removes shell exit status and contradicts the current operational skill and specification.

## What Changes

- Retain only the tail of an oversized tool result.
- Preserve the shell exit status in that tail for both execution paths.
- Add deterministic tests for oversized results with zero and nonzero exit codes.
- Update the operational skill and the main specification.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `bounded-tool-output`: Require tail-only inline results and visible shell exit status after truncation.

## Impact

Source requirements: [PRD-001 FR-011](../../../../docs/prd/PRD-001-netclaw-mvp.md#fr-011-tool-access) and
[PRD-006 MCP-003](../../../../docs/prd/PRD-006-mcp-tool-integration.md#mcp-003-policy-gating).

MVP scope includes shell result text, dispatcher tests, operational guidance, and the output contract.
New tools, approval scopes, configuration properties, and unbounded capture are out of scope.

## Security and operational impact

The dispatcher retains the current redaction, default-deny authorization, and session-scoped continuation gates.
Models can read the exit status without another tool call.
Operators retain the current capture ceiling and spill behavior.
