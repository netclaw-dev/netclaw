## 1. OpenSpec alignment

- [x] 1.1 Add proposal, design, and capability deltas for bootstrap seeding, loopback bearer auth, and CLI endpoint derivation.
- [x] 1.2 Cross-check the new deltas against existing `daemon-exposure`, `device-pairing`, and `hub-auth` requirements so reverse-proxy loopback auto-auth remains disabled.

## 2. Daemon bootstrap seeding

- [x] 2.1 Implement a daemon-owned first-launch bootstrap seeding path for remote-auth-required exposure modes.
- [x] 2.2 Persist a one-shot marker or equivalent state so auto-seeding stops after the first successful non-local daemon start.
- [x] 2.3 Ensure bootstrap seeding does not overwrite existing paired devices or an existing local device token.

## 3. Auth and endpoint resolution

- [x] 3.1 Update daemon auth selection so reverse-proxy mode still rejects implicit loopback auth but accepts bearer auth on loopback control-plane requests.
- [x] 3.2 Update CLI endpoint resolution to fall back to daemon bind config when no explicit endpoint override exists.
- [x] 3.3 Normalize wildcard daemon bind hosts to a connectable local control-plane endpoint.
- [x] 3.4 Update CLI token-attachment rules to use effective exposure requirements instead of loopback-only heuristics.

## 4. Wizard alignment

- [x] 4.1 Adjust wizard bootstrap seeding so it cooperates with daemon-owned first-launch bootstrap and does not overwrite existing bootstrap state.
- [x] 4.2 Keep onboarding health-check and pairing guidance aligned with the new runtime bootstrap behavior.

## 5. Tests and verification

- [x] 5.1 Add daemon tests covering first-launch bootstrap seeding, no-reseed after first successful non-local start, and preservation of existing device state.
- [x] 5.2 Add auth tests covering reverse-proxy loopback requests succeeding only with bearer auth.
- [x] 5.3 Add CLI tests covering daemon-bind fallback endpoint derivation, wildcard normalization, and loopback bearer-token attachment in reverse-proxy mode.
- [x] 5.4 Run `openspec validate "preserve-bootstrap-in-reverse-proxy"`.
- [x] 5.5 Run targeted code tests plus required repo quality gates for the touched code.
