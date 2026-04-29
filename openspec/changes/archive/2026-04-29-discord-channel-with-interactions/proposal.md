## Why

Netclaw's input architecture and PRD direction already treat channels as pluggable, but MVP runtime behavior remains Slack-only. Adding Discord now prevents Slack-specific coupling in routing, approvals, and command handling, and unlocks Discord-first operator workflows without weakening default-deny security.

Source PRDs: `PRD-009-input-adapters-and-unified-input.md`, `PRD-001-netclaw-mvp.md`, `PRD-002-gateway-security-envelope.md`, `PRD-003-operator-ux-ops-console.md`.

## What Changes

- Add a first-party Discord channel adapter with Slack-parity ingress, session routing, and thread-aware reply delivery.
- Support text-first slash-command compatibility in Discord message content so `/skill ...` dispatch works in MVP without requiring Discord app-command registration.
- Support interaction-based tool approval UX for Discord while requiring a deterministic text fallback path when interactions are unavailable or fail.
- Add an explicit offline test strategy for Discord paths so CI does not require a live Discord instance or external network dependency.
- Add planning artifact(s) under `docs/ui/` for desktop and mobile Discord tool-approval mockups, aligned to future operator UX delivery.

**In scope (MVP):** Discord gateway transport, ACL-gated ingress parity, session key derivation, reply delivery parity, text-first slash-command dispatch compatibility, interactive approval contract with deterministic text fallback, offline testability in CI, planning docs for approval UX mockups.

**Out of scope:** mandatory Discord app-command registration, Discord voice/stage support, role-management automation, cross-channel session merging, production web UI implementation.

## Capabilities

### New Capabilities

- `netclaw-discord-socket`: Discord channel requirements for gateway lifecycle, message normalization, ACL-gated dispatch, thread-aware reply delivery, interaction-capable approval rendering, and text-first slash-command compatibility.

### Modified Capabilities

- `netclaw-input-adapters`: Add Discord source metadata and entity-key routing semantics while preserving transport-agnostic actor/session boundaries.
- `slash-command-dispatch`: Require text-first slash-command parsing parity on Discord message content without dependency on platform-native app-command registration.
- `tool-approval-gates`: Extend approval interaction requirements for Discord interactions and deterministic text fallback behavior.
- `netclaw-testing`: Add CI-safe offline test requirements for Discord adapter and interaction fallback behavior.

## Impact

- **Affected systems:** channel runtime wiring, session routing metadata, slash-command interception path, tool-interaction request/response rendering, and test harnesses for channel adapters.
- **APIs/config:** adds Discord adapter configuration and diagnostics; no breaking Slack config change.
- **Security impact:** preserves default-deny gate behavior and fail-closed startup posture for Discord.
- **Operational impact:** adds Discord connection/approval fallback diagnostics; no live Discord dependency in required CI suites.
- **Planning artifacts:** introduces `docs/ui` mockup planning deliverable for Discord approval UX (desktop + mobile) with PRD traceability.
