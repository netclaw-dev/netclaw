## Context

Reverse-proxy hardening correctly removed loopback auto-auth from reverse-proxy mode, but that also removed the implicit first-launch bootstrap path that older local-only installs relied on. Today the init wizard partially patches around that by writing a bootstrap device into `devices.json`, but that behavior is CLI-owned, not daemon-owned, so manual config, Docker first boot, and any fresh setup that bypasses the wizard can still deadlock on startup validation.

The CLI also still assumes loopback endpoints never need bearer auth. That was true when loopback always implied local operator trust, but it stops being true once reverse-proxy mode intentionally disables loopback auto-auth. The daemon host still needs a usable local control-plane path for pairing and management, and the CLI must be able to authenticate to that path with the bootstrap device token when required.

This change spans daemon startup validation, auth selection, bootstrap persistence, and CLI endpoint resolution. It is security-sensitive because the fix must restore first-launch usability without relaxing reverse-proxy trust boundaries.

## Goals / Non-Goals

**Goals:**

- Preserve a working first-launch bootstrap path for setup-owned installs in non-local exposure modes.
- Ensure Docker/manual first boot gets the same bootstrap behavior as the init wizard.
- Keep reverse-proxy loopback auto-auth disabled for ordinary connections.
- Allow daemon-host control-plane clients to authenticate to loopback endpoints with a bearer token when exposure mode requires remote authentication.
- Resolve a usable CLI control-plane endpoint from daemon bind config when no explicit override exists.
- Avoid broad exposure-mode redesign while closing the immediate issue #866 usability gap.

**Non-Goals:**

- Redesigning exposure modes or trust boundaries beyond the issue #866 fix.
- Adding a new remote authentication scheme.
- Making remote clients automatically discover public/tunnel endpoints.
- Replacing the existing pairing flow.
- Changing reverse-proxy forwarded-header trust rules from issue #862/#866 hardening.

## Decisions

### D1. Bootstrap seeding moves to a daemon-owned first-launch service

The daemon will own first-launch bootstrap seeding for remote-auth-required modes. Before the first successful non-local startup completes, if no paired devices exist and the install is still setup-owned, the daemon seeds a local paired device/token and persists the token into local client secrets.

Rationale:

- Wizard-only seeding is too narrow and misses Docker/manual first boot.
- Startup validation and bootstrap persistence must live under the same runtime contract.
- Daemon ownership lets the wizard become an optional producer of config, not the sole producer of first-launch auth state.

Alternative considered:

- Keep bootstrap logic in `netclaw init` only. Rejected because it leaves non-wizard setup paths broken.

### D2. Bootstrap seeding is one-shot and gated before the first successful non-local start

The daemon will seed only when all of the following are true:

- the configured exposure mode requires remote authentication
- no paired device currently exists
- no successful non-local start has been recorded yet
- the install is considered setup-owned/local to the daemon host

After the daemon reaches `ApplicationStarted` successfully in a non-local mode, it records that first-launch bootstrap is complete and stops auto-seeding on future starts.

Rationale:

- This preserves first-launch usability without silently recreating credentials after operators intentionally revoke them later.
- Recording completion at successful startup matches the issue statement exactly.

Alternative considered:

- Re-seed whenever the registry becomes empty. Rejected because that would silently override operator intent and weaken revocation semantics.

### D3. Reverse-proxy mode keeps loopback auto-auth disabled, but bearer auth may authenticate loopback control-plane requests

`LoopbackAuthenticationHandler` remains disabled for reverse-proxy mode. The auth selector will still prefer bearer auth whenever an `Authorization: Bearer` header is present, including for loopback endpoints. CLI token attachment rules will be based on whether the endpoint requires remote authentication, not just whether the URI host is loopback.

Rationale:

- This preserves the hardening boundary: loopback by itself does not imply operator trust in reverse-proxy mode.
- It restores a safe local control-plane path by requiring an explicit paired-device credential.

Alternative considered:

- Re-enable loopback auto-auth for daemon-host loopback only. Rejected because that would reopen the implicit trust path the hardening work intentionally removed.

### D4. CLI endpoint resolution falls back to daemon bind config when no explicit endpoint override exists

When `NETCLAW_DAEMON_ENDPOINT` and client config are absent, the CLI will read daemon config and construct a local control-plane endpoint from `Daemon.Host` and `Daemon.Port`. If the daemon bind host is unspecified or wildcard (`0.0.0.0`, `::`, `[::]`), the CLI will normalize that to a loopback client endpoint (`127.0.0.1`) because wildcard binds are not valid connect targets.

Rationale:

- A daemon-host CLI needs a connectable endpoint that reflects the daemon’s local bind config.
- Wildcard binds are server-side listen addresses, not client addresses.

Alternative considered:

- Keep defaulting to `127.0.0.1:5199` only. Rejected because it ignores operator bind config and breaks non-default local control-plane ports/hosts.

### D5. Wizard bootstrap becomes best-effort, not authoritative

The wizard may still contribute bootstrap device/token state, but daemon-owned first-launch seeding becomes the source of truth. Wizard writes must not conflict with or overwrite an existing daemon-owned bootstrap credential.

Rationale:

- Keeps current init UX working while preventing split-brain bootstrap ownership.
- Minimizes churn in wizard tests and flows.

Alternative considered:

- Remove all wizard bootstrap behavior immediately. Rejected because a smaller transition is safer and easier to validate.

## Risks / Trade-offs

- [Risk] Auto-seeding a local device token writes a sensitive credential during first boot. -> Mitigation: reuse existing secrets/device persistence mechanisms and only seed once before first successful non-local startup.
- [Risk] Determining whether an install is still setup-owned could be ambiguous. -> Mitigation: scope the implementation to local daemon-owned config/secrets paths and fail closed by skipping seeding when ownership cannot be established confidently.
- [Risk] Endpoint normalization for wildcard binds could surprise operators who expected a remote host value. -> Mitigation: use it only as a local fallback when no explicit override exists.
- [Risk] Wizard and daemon bootstrap paths could both attempt to seed on the same install. -> Mitigation: centralize existence checks and avoid overwriting existing paired devices or device tokens.
- [Risk] Allowing bearer auth on loopback may be misread as re-enabling loopback trust. -> Mitigation: keep the `hub-auth` contract explicit that credentials, not loopback origin, authorize these requests in remote-auth-required modes.

## Migration Plan

1. Add spec deltas for bootstrap seeding, auth selection, and CLI endpoint resolution.
2. Implement a daemon-owned bootstrap seeding service plus a persisted first-success sentinel.
3. Update CLI endpoint and token-resolution rules to honor daemon bind config and remote-auth-required loopback endpoints.
4. Adjust wizard bootstrap behavior so it no longer acts as the only first-launch path.
5. Add regression tests for manual/Docker first boot, reverse-proxy loopback bearer auth, and fallback endpoint derivation.

Rollback:

- Revert the bootstrap seeding service and CLI auth-selection changes together.
- Operators can still pair manually through an already working local install path if rollback is required.

## Open Questions

- What is the narrowest reliable signal for “setup-owned install” in the current codebase without introducing a broader install-ownership model?
- Should the bootstrap device name be deterministic (for example `<machine>-bootstrap`) or continue using the machine name when seeding locally?
- Should the first-success sentinel live in config state, device state, or a separate bootstrap marker file?
