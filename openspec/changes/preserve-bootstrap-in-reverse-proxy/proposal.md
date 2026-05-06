## Why

Issue #866 exposed a first-launch gap introduced by reverse-proxy hardening: setup-owned installs can now block their own initial control-plane access because reverse-proxy mode disables loopback auto-auth before any paired device exists. Netclaw needs to preserve the original bootstrap path for fresh installs without reopening general loopback trust in reverse-proxy mode.

## What Changes

- Preserve first-launch bootstrap for setup-owned installs by auto-seeding a local paired device/token before the first successful non-local daemon start when no paired devices already exist.
- Move bootstrap protection from wizard-only behavior to daemon/runtime-aware behavior so Docker and manual first boot get the same safe first-launch path.
- Keep reverse-proxy loopback auto-auth disabled for ordinary connections, but allow bearer-token authentication on loopback control-plane endpoints when the selected exposure mode requires remote authentication.
- Teach the CLI to derive a usable control-plane endpoint from daemon bind configuration when no explicit endpoint override exists, instead of assuming `http://127.0.0.1:5199`.
- Ensure the daemon-host pairing and management flows continue to work against reverse-proxy-safe local binds without requiring an exposure-mode redesign.
- Explicitly defer the broader exposure-mode redesign tracked in issue #868.

## Capabilities

### New Capabilities

- `daemon-bootstrap-pairing`: first-launch bootstrap seeding for setup-owned installs before the first successful non-local daemon start

### Modified Capabilities

- `daemon-exposure`: allow startup validation and local control-plane auth to preserve first-launch bootstrap in remote-auth-required modes without re-enabling general loopback auto-auth
- `device-pairing`: extend bearer-token auth and paired-device requirements to cover loopback control-plane endpoints in remote-auth-required modes
- `hub-auth`: narrow loopback auto-auth so reverse-proxy-safe loopback control-plane access can use bearer auth while ordinary loopback auto-auth remains disabled in reverse-proxy mode
- `netclaw-cli`: resolve a usable daemon endpoint from daemon bind config when no explicit client override exists, and attach bearer tokens when loopback endpoints require remote authentication
- `netclaw-onboarding`: align wizard-owned bootstrap seeding with daemon-owned first-launch bootstrap so setup flows do not fight the runtime contract

## Impact

- Affected code includes daemon startup validation, device registry/bootstrap token persistence, auth scheme selection, CLI endpoint resolution, and wizard bootstrap handoff.
- Operator-visible behavior changes on fresh non-local installs: the daemon can seed an initial local paired device/token before its first successful remote-auth-required start, and the local CLI can authenticate to loopback control-plane endpoints with that token when exposure mode requires it.
- Security impact: preserves reverse-proxy hardening by keeping general loopback auto-auth disabled, while restoring the original first-launch bootstrap path through an explicit paired-device credential instead of implicit loopback trust.
- Operational impact: Docker/manual first boot and daemon-host CLI commands become usable without a separate exposure-mode redesign or requiring operators to pre-stage an endpoint override.
- Out of scope: issue #868's broader exposure-mode redesign, changes to remote pairing UX beyond bootstrap preservation, and any new non-device authentication schemes.

### Source PRDs

- `PRD-002` (gateway security envelope; fail-closed and default-deny)
- `PRD-004` (CLI onboarding and operator diagnostics)
