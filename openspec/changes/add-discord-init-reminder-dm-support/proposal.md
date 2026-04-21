## Why

Discord support is in progress, but reminder delivery still centers on thread and
channel patterns that map cleanly to Slack and not to Discord direct messages.
We need reminder delivery and authorization semantics that make Discord DM
sessions first-class while preserving the same default-deny safety model and a
single `netclaw init` pipeline for operators.

Source PRDs: `PRD-001-netclaw-mvp.md`, `PRD-002-gateway-security-envelope.md`,
`PRD-004-cli-onboarding-and-config.md`, `PRD-008-scheduling-and-periodic-tasks.md`,
`PRD-009-input-adapters-and-unified-input.md`.

## What Changes

- Add Discord direct-message reminder delivery requirements so reminders can
  target and re-enter Discord DM sessions with canonical recipient identity,
  deterministic session identity, and fail-loud delivery behavior.
- Extend reminder authorization requirements to enforce Slack-like controls for
  Discord: explicit allow checks for sender and DM channel context, audience
  bounds at mint time, and no bypass around tool/data grant checks.
- Extend `netclaw init` requirements so onboarding can configure Discord gateway
  credentials and baseline Discord ACL policy in the same guided pipeline as
  existing Slack setup.
- Align reminder routing and input-adapter requirements to treat Discord DM
  sessions as transport-native entities without introducing transport-specific
  behavior in session actors.

**In scope (MVP):** Discord DM reminder delivery contract, Discord-specific ACL
requirements mirroring Slack posture, and init wizard capture/validation for
Discord config + ACL defaults.

**Out of scope:** Discord guild-channel reminder expansion beyond the existing
channel parity roadmap, role-management automation, and non-Discord changes to
reminder execution semantics.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: add Discord DM reminder delivery and session re-entry
  requirements, including canonical destination validation and delivery
  observability/failure semantics.
- `netclaw-acl`: add Discord DM authorization requirements with Slack-like
  default-deny controls for sender/channel checks and reminder audience bounds.
- `netclaw-input-adapters`: add Discord DM source metadata and entity-key
  routing requirements used by reminder execution paths.
- `netclaw-onboarding`: require `netclaw init` to collect/validate Discord
  credentials and generate Discord baseline ACL configuration in guided setup.

## Impact

- **Source code:** reminder target resolution, Discord gateway delivery wiring,
  ACL evaluation paths for Discord DM source metadata, and init-wizard step
  flow/config writers.
- **Config/runtime:** `netclaw init` output includes Discord adapter settings
  and Discord ACL starter policy when Discord is enabled.
- **Security impact:** preserves default-deny behavior by requiring explicit
  Discord sender/channel authorization and audience-bound reminder creation.
- **Operational impact:** introduces Discord DM reminder-delivery diagnostics and
  startup validation for missing/invalid Discord init inputs.
