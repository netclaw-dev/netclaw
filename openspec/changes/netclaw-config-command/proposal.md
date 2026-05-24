## Why

After install, operators need one main settings surface. That surface is
now locked as `netclaw config`, while `netclaw init` is reduced to
bootstrap-only setup. The existing planning drifted toward a flat list of
leaf editors and duplicated advanced policy controls that already belong to
other commands. This change realigns the plan around the locked product
shape:

- `netclaw config` is the main post-install settings surface.
- The root IA is domain-oriented, not a flat list of every leaf editor.
- Routed handoffs are acceptable for `Inference Providers -> netclaw provider`
  and `Models -> netclaw model` without a navigation-stack refactor.
- MCP permission editing routes to `netclaw mcp permissions`; it is not
  recreated inside `netclaw config`.

Source PRDs: `PRD-004-cli-onboarding-and-config.md`,
`PRD-001-netclaw-mvp.md`, `PRD-002-gateway-security-envelope.md`.

## What Changes

- Add a top-level `netclaw config` command that launches a domain-oriented
  navigation dashboard rather than a flat registry dump.
- The root dashboard SHALL include these areas for this branch:
  - Inference Providers
  - Models
  - Channels
  - Inbound Webhooks
  - Skill Sources
  - Search
  - Browser Automation
  - Telemetry & Alerting
  - Security & Access
- Routed handoffs are first-class for:
  - `Inference Providers` -> `netclaw provider`
  - `Models` -> `netclaw model`
  No back-stack refactor is required in this branch.
- `Channels` contains Slack, Discord, Mattermost.
- `Skill Sources` contains External Skills and Skill Feeds.
- `Telemetry & Alerting` contains Telemetry and Outbound Webhooks only in
  this pass. Delivery policy tuning is deferred.
- `Security & Access` contains Security Posture, Enabled Features,
  Audience Profiles, and Exposure Mode.
- Leave MCP Servers out of scope for this branch. Any MCP permissions,
  grants, or approval editing SHALL route to `netclaw mcp permissions`.
- Keep posture values to `Personal`, `Team`, and `Public` only.
- Keep Security Posture, Enabled Features, and Audience Profiles as
  separate concepts:
  - Security Posture: selects the high-level operating stance.
  - Enabled Features: deployment-wide runtime enablement.
  - Audience Profiles: curated high-level per-audience editor.
- Audience Profiles SHALL remove per-audience feature toggles and
  per-audience shell mode. Audience Profiles SHALL focus on:
  - Tool Access (non-MCP)
  - File Access
  - Incoming Attachments
  - Reset to posture default
- `Reset to posture default` / posture overwrite SHALL reset the full
  underlying audience profile, including hidden MCP/approval settings for
  that audience.
- Exposure Mode is edited under `Security & Access` and retains the
  existing `Daemon` config shape. Modes remain explicit:
  `Local`, `Reverse Proxy`, `Tailscale Serve`, `Tailscale Funnel`,
  `Cloudflare Tunnel`.
- Each non-local exposure mode gets its own mode-specific dialog. `Local`
  requires no extra setup.
- Keep a single active selector via `Daemon.ExposureMode`; do not add
  per-mode active flags. Preserve inactive old values in config and ignore
  them when inactive.
- Do not add or persist new exposure-specific fields that do not already
  fit the current config shape.
- First-time enablement of a non-local exposure mode from `netclaw config`
  SHALL auto-pair the current configuring client if no bootstrap/pairing
  state exists yet.
- If existing bootstrap state is orphaned or mismatched, the editor SHALL
  block and point the operator to `netclaw doctor`, the formal docs, and
  issue `#875`. No inline repair is in scope.
- `netclaw config` on a missing install SHALL refuse with a plain non-zero
  message directing the operator to `netclaw init`. No partial TUI renders.
- Validation is generalized across leaf editors: each leaf validates what
  it edits before save, including local references and external probes when
  relevant. Structurally invalid config remains non-overridable; runtime or
  probe failures MAY offer `Save anyway`.
- Round-trip preservation and test assertions are semantic, not
  byte-identical.
- Leaf editors receive substantive round-trip and smoke coverage. Routed
  handoffs receive shallow routing coverage only.

**In scope (MVP):** `netclaw config`, domain-oriented dashboard IA, routed
handoffs for providers/models, leaf editors for the in-scope areas above,
generalized validation behavior, exposure-mode dialogs within the existing
config shape, missing-install refusal, and coverage aligned to leaf-vs-
routed responsibilities.

**Out of scope:** Identity editing, MCP Servers, MCP permissions editing
inside config, delivery-policy tuning, config-stack/back-stack redesign,
new exposure-specific persisted fields, inline bootstrap repair, and any
config-shape rearrangement of the existing `Daemon` or global shell mode
sections.

## Capabilities

### New Capabilities

- `netclaw-config-command`: contract for the domain-oriented config
  dashboard, routed handoffs, leaf-editor hosting, generalized validation,
  missing-install refusal, and coverage expectations.

### Modified Capabilities

- `netclaw-cli`: add `netclaw config` as a top-level settings command.
- `feature-selection-wizard`: move post-install runtime enablement editing
  to the `Enabled Features` leaf under `Security & Access`, while keeping
  init bootstrap behavior aligned to posture.

## Impact

**Affected systems:**

- CLI routing for `netclaw config`.
- Termina config dashboard and sub-pages.
- Section-editor hosting for in-scope leaves.
- Routed handoff affordances for provider/model commands.
- Exposure-mode editing and validation.
- Test surface for leaf editors, routing coverage, and generalized save
  validation.

**Security and operational impact:**

- Ongoing settings now have one primary post-install home.
- Audience Profiles no longer duplicate MCP permissions or raw low-level
  policy editing.
- Exposure-mode changes keep the existing config shape and preserve
  inactive values.
- Validation behavior is generalized beyond issue `#1151`; structural
  invalidity still blocks writes, while runtime reachability failures can
  be overridden with `Save anyway`.
