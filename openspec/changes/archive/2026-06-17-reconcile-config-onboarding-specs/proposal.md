## Why

The `netclaw config` rewrite and `netclaw init` simplification shipped on
`docs/netclaw-validated-ui-components`, but a spec-vs-code audit found the canonical
OpenSpec specs drifted from what was actually built. Most acutely, `netclaw-onboarding`
still mandates a 9-step wizard with a Memory/Memorizer step that does not exist, and
`channel-audience-tui` describes a block-on-API-failure flow that the code replaced with
save-and-flag. These specs are referenced when extending the surface, so the drift will
mislead future work (and risks reintroducing a fixed lockout or ACL gap by trusting a
stale spec). This change reconciles the specs to the as-built, shipped behavior. (PRD-004.)

## What Changes

This change modifies requirements in existing specs to match shipped code. There is **no
production code change** — the implementation already exists and is covered by tests.

- **netclaw-onboarding**: remove the obsolete Memory/Memorizer step and all 9-step
  (`TotalSteps SHALL be 9`) language; document the actual 5-step flow (Provider → Identity
  → Security Posture → Enabled Features [Personal skips] → Health Check); Identity collects
  4 substeps (agent name, communication style, operator name, timezone) — not
  workspaces/webhook; Health Check auto-launches `netclaw chat` on success (no Enter gate);
  add container-supervisor failure messaging; correct the Phase-2 identity file to SOUL.md
  and mark environment-discovery / project-registration as deferred. **BREAKING (spec
  only)**: removes the Memory-provider-selection and Memorizer-MCP requirements.
- **channel-audience-tui**: replace "block on Slack API failure" with the two-tier
  behavior (a genuine probe failure blocks the save; unresolved channel names persist and
  are flagged non-blockingly — an unresolved name in the allow-list is inert); add Slack
  name→ID normalization and secret blank-preserve; replace the type-to-filter search with
  the resolve-before-add single-entry flow.
- **netclaw-config-command**: add `Workspaces Directory` as a dashboard area; document the
  directory pickers (Skill Sources local folder, Workspaces); add Inbound Webhooks behavior
  (enable toggle + execution timeout + no-routes advisory); add Search progressive
  disclosure; name Mattermost as a supported adapter. (Bootstrap-exposure auto-pair is now
  accurate in code and is left unchanged.)
- **security-posture-tui**, **feature-selection-wizard**, **inbound-webhooks**: minor
  corrections (step ordering/labels, audience-default ownership, posture cascade, Personal
  omit-flags + auto-open Features, `Webhooks.ExecutionTimeoutSeconds` + no-routes advisory).

## Capabilities

### New Capabilities

None — this is a pure reconciliation of existing capabilities.

### Modified Capabilities

- `netclaw-onboarding`: remove Memory/Memorizer + 9-step requirements; restate the 5-step
  flow, 4-substep Identity, health-check auto-launch, supervisor-failure messaging, and the
  SOUL.md identity file.
- `channel-audience-tui`: block→save-and-flag on channel resolution; name→ID normalization;
  secret blank-preserve; resolve-before-add channel entry.
- `netclaw-config-command`: Workspaces Directory area; directory pickers; inbound-webhooks
  enable/timeout/advisory; Search progressive disclosure; Mattermost adapter.
- `security-posture-tui`: Provider-step ordering (no "ChatServices" step); audience defaults
  owned by the channel picker step; posture-change cascade confirmation.
- `feature-selection-wizard`: Personal posture omits Enabled flags (schema defaults); editor
  auto-opens Enabled Features after a non-Personal posture save.
- `inbound-webhooks`: add `Webhooks.ExecutionTimeoutSeconds` and the no-routes advisory.

## Impact

- **Specs only.** No production code, API, or schema changes — the behaviors are already
  implemented and tested on `docs/netclaw-validated-ui-components`.
- Affected specs: `openspec/specs/{netclaw-onboarding, channel-audience-tui,
  netclaw-config-command, security-posture-tui, feature-selection-wizard,
  inbound-webhooks}/spec.md`.
- **Security & operational**: net-zero behavior change. Reconciliation makes the
  default-deny exposure/pairing and channel-ACL requirements describe what the code actually
  enforces (unresolved channel names are inert; the configuring client is auto-paired on
  non-local exposure), reducing the risk that a future edit reintroduces a lockout or a
  silent ACL gap by trusting a stale spec.
