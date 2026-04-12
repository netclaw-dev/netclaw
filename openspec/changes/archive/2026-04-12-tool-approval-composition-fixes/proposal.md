## Why

Tool approval behavior in the shipped pipeline has three composition gaps that can
produce surprising security outcomes: control-plane file mutations can miss
intended approval overrides, approve-once retries can reprompt because matching
uses pre-filter patterns, and shell policy layering between operation-level and
resource-level hard denies is under-specified. This follow-up closes those gaps
to keep approval gates deterministic and auditable under PRD-002 security
constraints.

## What Changes

- Define deterministic approval-mode key precedence for matcher-derived keys
  (for example `file_write:control-plane`) versus base tool keys (`file_write`)
  and default mode fallback.
- Align approve-once retry matching with the same filtered unapproved pattern
  set that was shown to the user in the prompt, including path-aware matcher
  patterns for control-plane file mutations.
- Clarify shell policy composition so operation hard-deny and resource hard-deny
  are both enforced with explicit precedence and denial reasons.
- Add targeted behavior scenarios in the capability spec for key precedence,
  approve-once retry behavior, and shell deny composition.
- In scope: tool approval composition and requirement/test updates in the
  existing approval capability.
- Out of scope: new approval UX options, non-tool interaction types, sandbox
  shell implementation, and broad ACL model redesign.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `tool-approval-gates`: Refine approval key resolution precedence,
  approve-once retry matching semantics, and shell hard-deny composition
  semantics; extend normative scenarios for these behaviors.

## Impact

- **Security / policy surface**: `ToolAccessPolicy`, `DispatchingToolExecutor`,
  matcher implementations, and shell deny checks gain explicit composition
  rules (PRD-002: SEC-003, SEC-006, SEC-009).
- **Behavioral consistency**: Approval prompts and immediate retries use the
  same pattern identity set, reducing false reprompts.
- **Operational clarity**: Deny reason precedence is documented for actor logs,
  tool audit entries, and troubleshooting.
- **Validation impact**: Update capability scenarios and matching tests in
  approval gate and executor suites; no config schema changes expected.
