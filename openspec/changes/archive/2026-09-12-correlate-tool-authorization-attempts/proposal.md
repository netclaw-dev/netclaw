## Why

Operators cannot reliably join a tool call to its authorization policy evaluation, correction, approval prompt, decision, retry, and final result. Existing log fields are incomplete across live sessions, recovered sessions, and sub-agents, which makes approval-spam diagnosis slow and can produce incorrect conclusions from unrelated events.

## What Changes

- Assign a PII-free `AuthorizationAttemptId` before each tool call's first authorization evaluation.
- Preserve that identifier across policy evaluation, agent correction, approval prompt, user decision, same-call retry, cold recovery, and final result.
- Emit the identifier as structured telemetry for parent sessions and sub-agents, with the session and provider call identifiers that are already available at each boundary.
- Persist the identifier with a pending approval so a recovered session continues the same correlation chain.
- Recover older pending approvals that do not contain the new field without changing their authorization meaning.
- Keep the identifier diagnostic only: it never grants access, selects an approval, or changes policy behavior.

In scope for this change: local and MCP tool authorization telemetry, interactive approval lifecycle telemetry, sub-agent approval bridging, journal compatibility, deterministic tests, and operator documentation.

Out of scope for this change: changing approval policy, expanding automatic approvals, changing grant matching, recording tool arguments or results, or exposing the identifier to the model as a tool contract.

Source requirements: PRD-002 SEC-007 and PRD-006 tool invocation auditing.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: Correlate each policy evaluation and interactive approval lifecycle with one PII-free authorization-attempt identifier.
- `netclaw-tools`: Correlate tool start and completion telemetry with the same authorization-attempt identifier without changing tool-visible contracts.

## Impact

- Affected components: tool execution abstractions, session tool pipeline, session persistence protocol and protobuf mapping, parent approval bridge, sub-agent execution, and structured logs.
- API impact: no model-facing tool schema change and no breaking public API change. Persisted protobuf messages gain optional additive fields.
- Dependency impact: none.
- Security impact: improves auditability without adding authority. Missing or malformed identifiers must never bypass a policy check.
- Operational impact: operators can query one identifier to reconstruct a complete authorization attempt without searching argument or result content.
