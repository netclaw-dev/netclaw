## Why

A clean Personal install writes an explicit `shell_execute = Approval` override. The runtime also applies this fail-closed result when the override is absent, but the specification and doctor describe `Auto` instead.

This conflict makes diagnostics incorrect and obscures the deployed security contract before PR #1733 changes shell parsing. This change aligns the contract before the approval matrix work begins.

## What Changes

- State that Personal shell execution requires approval unless an explicit shell override selects `Auto` or `Deny`.
- Keep the clean Personal install as the normal configuration source.
- Keep the runtime fallback as a safety backstop for legacy or partial configuration.
- Fix the doctor check so it reports the effective runtime mode.
- Add focused doctor regression tests.
- Keep the approval matrix and PR #1733 changes out of scope.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: Align the Personal shell fallback requirement with the shipped runtime and generated configuration.
- `netclaw-cli`: Make the doctor warn for an explicit Personal shell `Auto` override, not for the fail-closed fallback.

## Impact

- **PRDs:** PRD-002 gateway security and PRD-004 CLI onboarding and configuration.
- **Code:** `ToolAudienceProfilesDoctorCheck` effective-mode diagnostics.
- **Tests:** Focused doctor tests for missing and explicit Personal shell modes.
- **Documentation:** Correct the approval runbook for existing configurations.
- **Security:** The change preserves fail-closed shell behavior. It adds no new grant or bypass.
- **Operations:** Operators stop receiving a false missing-gate warning for a configuration that the runtime gates.
- **MVP scope:** The change corrects the existing contract. It adds no new capability.
