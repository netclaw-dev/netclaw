## Why

Netclaw onboarding currently treats provider setup as mostly static credential entry, which creates friction for OAuth-first providers and leaves operators guessing when model discovery or diagnostics fail. We need an explicit OAuth-first onboarding flow now to satisfy PRD-004 guided setup and PRD-005 multi-provider resilience with actionable recovery paths.

## What Changes

- Add explicit provider-selection decision trees in Termina onboarding, including OAuth-capable vs API-key-only provider branches.
- Add OAuth device flow onboarding behavior with clear step states (start, code display, polling, success/failure, retry/cancel).
- Add model discovery decision trees with deterministic fallback paths when provider catalogs are unavailable or incomplete.
- Extend `netclaw doctor` follow-up checks to validate onboarding outputs (provider auth state, resolved model selection, fallback readiness).
- Keep OpenRouter as default but support OAuth-first provider onboarding without weakening secret-safe handling or default-deny posture.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `netclaw-onboarding`: onboarding requirements gain explicit decision trees for provider selection, auth branching, OAuth device flow steps, and model discovery fallback handling.
- `netclaw-model-providers`: provider requirements gain OAuth device authorization path semantics, model-catalog fallback behavior, and diagnostics visibility for degraded discovery/auth states.
- `netclaw-cli`: CLI requirements gain doctor follow-up checks tied to onboarding/provider outcomes and remediation-first output for OAuth/model resolution failures.

## Impact

- **Code/Runtime**: onboarding workflow orchestration, provider auth abstractions, model discovery pipeline, and doctor check composition in CLI application layers.
- **Security**: no clear-text credential leakage; OAuth tokens handled with the same secret-safe guarantees as API keys; fail-closed behavior when auth cannot be established.
- **Operations**: improved first-run success and faster troubleshooting through explicit stateful diagnostics and remediation commands.
- **Dependencies/APIs**: no new external runtime channel; provider integrations may require additional OAuth metadata endpoints or device-authorization settings per provider profile.
- **Traceability**: maps directly to `PRD-004` (guided/reentrant onboarding + diagnostics) and `PRD-005` (provider strategy, validation, fallback, observability).
