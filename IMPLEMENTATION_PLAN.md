# Netclaw Implementation Plan

Last updated: 2026-05-31

This is the execution plan for Netclaw. Autonomous agents and RALPH-style loops
SHALL work from `NOW` by default. `NEXT` and `LATER` work belongs in
`BACKLOG_PARKING_LOT.md` unless the user explicitly reprioritizes it.

## Operating Principle: Swing Through The Ball

A task is not done when the local component accepts input, renders a screen, or
writes a file. A task is done when the downstream runtime path consumes the
produced artifact successfully, or bad input is rejected before it crosses the
boundary.

Examples:

- A config editor is done only when runtime startup/ACL/routing consumes the
  saved shape it emits.
- A TUI flow is done only when typed input, paste input, persisted state,
  re-entry, and semantic smoke assertions all agree.
- A tool or adapter is done only when policy denial, invalid credentials,
  missing resources, and happy-path dispatch are all covered.
- A planning task is done only when PRD, spec, OpenSpec, tests, docs, and skill
  guidance point at the same behavior.

## Verification Levels

Use the highest level required by the task. Higher levels include the lower
levels unless explicitly stated otherwise.

| Level | Name | Required proof |
|-------|------|----------------|
| L0 | Planning-only | PRD/spec/docs updated; no runtime behavior changed. |
| L1 | Unit/contract | Targeted unit or contract tests prove pure behavior, serialization, validation, mapping, or policy decisions. |
| L2 | Integration | Component integration tests prove real persistence, DI, actor lifecycle, config binding, or fake-provider boundaries. |
| L3 | Interactive/smoke | Native smoke tape, CLI/TUI smoke, or equivalent real binary exercise proves the user-visible path. |
| L4 | Live/demo/e2e | Aspire demo, live provider, Docker image, or full runtime flow proves external/runtime wiring. |

## Non-Negotiable Quality Gates

These gates apply to every `NOW` task unless the task explicitly says why a gate
does not apply.

- [ ] **Discovery gate:** Read the matching PRD, spec, OpenSpec capability, and
  active change plan before coding.
- [ ] **Consumer gate:** Name the downstream consumer of any config, event,
  actor message, persisted record, tool schema, or protocol payload the task
  writes.
- [ ] **Canonical representation gate:** Prove the producer emits the exact
  representation expected by the consumer, not merely a schema-valid value.
- [ ] **Negative-path gate:** Add at least one invalid/unresolved/denied test for
  every security-relevant or routing-relevant input.
- [ ] **No silent fallback gate:** Misconfiguration fails visibly; fallback is
  allowed only when partial failure is normal runtime behavior.
- [ ] **Schema gate:** Any `*Config` property change updates
  `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`.
- [ ] **TUI gate:** Termina/init/config changes run the native smoke harness and
  include semantic assertions, not just screen text.
- [ ] **Runtime gate:** If config drives runtime behavior, verify startup,
  runtime binding, ACL, routing, or tool execution consumes the saved config.
- [ ] **Docs/spec gate:** Behavior changes update the relevant docs/specs and
  any mapped system skill.
- [ ] **Repository gates:** Run `dotnet test`, `dotnet slopwatch analyze`,
  `pwsh ./scripts/Add-FileHeaders.ps1 -Verify`, and `git diff --check` unless
  the task explicitly scopes to docs-only work.

## Automation-First QA Floor

The human bare minimum should be priority calls, secrets/credentials for live
checks, and occasional high-risk UX spot checks. Agents are responsible for the
automatable proof below.

| Recent bug class | Required automation | Human minimum |
|------------------|---------------------|---------------|
| Typed input does not reach a TUI field | Headless Termina test with `VirtualInputSource` covering typed characters, paste, Tab/focus movement, Enter/submit, Escape/back. Critical flows also need a native VHS smoke tape. | Run or review one live command only when a real terminal/TTY bug is suspected. |
| Dynamic validation does not run | Fake-provider failure test proving save is blocked, persistence is unchanged, and the visible error is shown. Tests must call the same public save path the UI uses. | Provide real provider credentials only for optional live probes. |
| Old config paradigm not ported to new editor | Load/round-trip tests from existing config and secrets into the new editor model, then back to disk. Tests must assert dormant values and secrets are preserved unless reset/delete is explicit. | Confirm whether stale fields should migrate, preserve, or fail. |
| Config shape accepted but runtime cannot consume it | Contract test between editor/init output and runtime options/ACL/routing/startup consumer. Assert canonical IDs/names/permissions, not just schema validity. | Decide behavior for ambiguous external API cases. |
| Smoke passes while semantic behavior is wrong | Smoke assertion script checks canonical persisted values, encrypted secrets, runtime-visible config, and error states. Screen text alone is not enough. | Review smoke artifact only if the assertion fails or UX changed substantially. |
| Async UI action fails silently | Public async method has direct tests; fire-and-forget handlers catch exceptions and surface status errors. Test fake exceptions from validation/save dependencies. | None by default. |
| Secret rotation/reset reintroduces old behavior | Tests cover blank-preserve, nonblank-replace, disable-preserve, reset-delete-immediate, and reopen-after-reset. | Confirm destructive copy in the UI. |

Minimum automation by surface area:

| Surface | Minimum gate |
|---------|--------------|
| Config editor | Static validation test, dynamic fake-failure test, existing-config round-trip test, config-to-runtime consumer test, native smoke for visible TUI paths. |
| Init wizard | Headless typed-input test for each prompt kind, native `init-wizard` smoke, existing-install path test, destructive-action double-confirm test. |
| Channel adapter | Options-binding test, ACL allow/deny tests, malformed/missing credential test, reply/routing integration or opt-in live smoke. |
| Tool/MCP | Schema generation test, schema coercion negative test, permission allow/deny/prompt tests, malformed metadata test. |
| Persistence/memory/session | Serialization round-trip, restart/recovery test, corrupt/missing state test, eval suite when prompt/memory behavior changes. |
| Packaging/demo | Install smoke, Docker image binary/version check, health endpoint check, opt-in demo smoke when runtime wiring changes. |

Manual-only acceptance criteria are not allowed for `NOW` implementation tasks.
If something truly cannot be automated, the task must say why and must provide
the smallest repeatable manual script plus expected output.

## Current Source Artifacts

- Product: `PROJECT_CONTEXT.md`, `docs/prd/README.md`, `docs/prd/PRD-001-netclaw-mvp.md`
- CLI/config: `docs/prd/PRD-004-cli-onboarding-and-config.md`, `docs/spec/SPEC-004-cli-contract.md`, `docs/spec/SPEC-007-guided-onboarding.md`, `openspec/specs/netclaw-config-command/spec.md`, `openspec/changes/netclaw-config-command/tasks.md`
- Security/gateway: `docs/prd/PRD-002-gateway-security-envelope.md`, `docs/spec/SPEC-001-runtime-boundaries.md`, `docs/spec/SPEC-003-acl-policy-and-security-controls.md`, `openspec/specs/netclaw-acl/spec.md`, `openspec/specs/netclaw-gateway-security/spec.md`
- Input adapters: `docs/prd/PRD-009-input-adapters-and-unified-input.md`, `openspec/specs/netclaw-input-adapters/spec.md`, `openspec/specs/netclaw-slack-socket/spec.md`, `openspec/specs/netclaw-discord-socket/spec.md`, `openspec/changes/add-mattermost-channel/tasks.md`
- Models/providers: `docs/prd/PRD-005-model-provider-strategy.md`, `docs/spec/SPEC-008-model-provider-abstraction.md`, `openspec/specs/netclaw-model-providers/spec.md`
- MCP/tools: `docs/prd/PRD-006-mcp-tool-integration.md`, `openspec/specs/netclaw-mcp/spec.md`, `openspec/specs/netclaw-tools/spec.md`, `openspec/specs/tool-approval-gates/spec.md`
- Memory/personality: `docs/prd/PRD-007-agent-personality-and-local-memory.md`, `openspec/specs/netclaw-agent-memory/spec.md`, `openspec/specs/project-instructions/spec.md`
- Scheduling: `docs/prd/PRD-008-scheduling-and-periodic-tasks.md`, `openspec/specs/netclaw-scheduling/spec.md`, `openspec/specs/reminder-execution-history/spec.md`
- Testing: `docs/spec/SPEC-010-testing-and-smoke-strategy.md`, `TOOLING.md`

## NOW

### Phase 0: Execution Governance

Purpose: prevent shallow local fixes from being mistaken for runtime-complete
work.

#### Task 0.1: Enforce the cross-boundary contract rule

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**Spec:** `docs/spec/SPEC-010-testing-and-smoke-strategy.md`
**Surface area:** cross-cutting
**Verification:** L0

Done when:

- [x] `AGENTS.md` references `IMPLEMENTATION_PLAN.md` as a read-first artifact.
- [x] `AGENTS.md` includes the Cross-Boundary Contract Rule.
- [x] This plan is the default routing artifact for build work.
- [x] `BACKLOG_PARKING_LOT.md` exists for non-now work.

#### Task 0.2: Add PRD/status traceability to the plan workflow

**PRD:** `docs/prd/README.md`
**Spec:** `docs/spec/SPEC-010-testing-and-smoke-strategy.md`
**Surface area:** docs
**Verification:** L0

Done when:

- [x] Every `NOW` task has a `PRD` reference.
- [x] Tasks with stale, missing, or conflicting PRD coverage are blocked until
  the PRD/spec is updated.
- [x] If a task changes OpenSpec-covered behavior, the corresponding OpenSpec
  workflow is used rather than hand-editing change artifacts.

#### Task 0.3: Add contract-test inventory for critical producer/consumer pairs

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**Spec:** `docs/spec/SPEC-010-testing-and-smoke-strategy.md`
**Surface area:** cross-cutting
**Verification:** L1

Done when:

- [x] Document the critical producer/consumer pairs in this plan or a linked
  spec, including config editor -> runtime options, channel events -> ACL,
  scheduler -> delivery gateway, tool schemas -> model/tool dispatcher, and
  memory persistence -> prompt assembly.
- [x] For each pair, identify the canonical representation and the test file
  that proves it.
- [x] Add missing tests or add explicit `NOW` tasks for gaps.

Inventory: `docs/spec/SPEC-010-testing-and-smoke-strategy.md` -> Critical
Producer/Consumer Contract Inventory. Remaining proof gaps are assigned to
explicit `NOW` tasks 3.1, 4.2, and 5.2-5.3.

#### Task 0.4: Automate recent regression classes

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`, `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `docs/spec/SPEC-010-testing-and-smoke-strategy.md`, `openspec/specs/netclaw-config-command/spec.md`
**Surface area:** testing, TUI, config
**Verification:** L3

Done when:

- [x] Every config/TUI task touching text input includes headless typed-input
  tests for typed characters, paste, Tab, Enter, Escape, and re-entry when
  applicable.
- [x] Every config leaf with dynamic validation has a fake-failure test proving
  validation runs before persistence and leaves files unchanged.
- [x] Every config leaf ported from init/old editor paths has an existing-config
  load/round-trip test covering dormant values and persisted secrets.
- [x] Every smoke tape with config writes has an assertion script that checks
  canonical semantic output, not only screenshots or text.
- [x] Any async UI save/test action has a direct awaitable test path plus
  fire-and-forget exception surfacing.

#### Task 0.5: Add audit tests for plan-critical config editors

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `openspec/specs/section-editor-abstraction/spec.md`, `openspec/specs/netclaw-config-command/spec.md`
**Surface area:** testing, config
**Verification:** L1

Done when:

- [x] A registry/audit test lists config leaf editors and fails when a visible
  editor lacks round-trip coverage.
- [x] The audit requires each visible editor to declare whether it has dynamic
  validation and, if yes, the test class that covers fake-failure behavior.
- [x] The audit requires each editor that writes secrets to have blank-preserve,
  nonblank-replace, and explicit-delete coverage.
- [x] The audit requires each editor that writes runtime-consumed config to name
  the runtime consumer and contract test file.

### Phase 1: Config Command And Channel Runtime Contracts

Purpose: finish the active config work all the way through runtime semantics.

`netclaw config` owns post-install tuning. It should cover ordinary changes an
operator might make after first run without re-entering bootstrap:

- Providers and Models route to their dedicated editors.
- Channels, Search, Security & Access, Exposure Mode, Skill Sources,
  Telemetry & Alerting, Workspaces Directory, Inbound Webhooks, and Browser
  Automation must not remain root-dashboard placeholders before this phase
  closes.
- Identity/personality re-entry remains `netclaw init` / identity-owned work;
  config may expose the Workspaces Directory because operators can move project
  discovery roots after first run without regenerating identity files.
- Per-session project switching is runtime state owned by the
  `set_working_directory` tool and the Audience Profiles `Change workspace`
  permission, not a global config editor.
- General MCP server/permission editing remains `netclaw mcp`; Browser
  Automation config may add/remove the canonical browser MCP profile, then route
  grants to `netclaw mcp permissions`.
- Inbound webhook route-file authoring remains `netclaw webhooks` / route files
  for this pass; config owns global enablement, execution timeout, route-count
  visibility, and loud diagnostics when enabled with no routes.
- Advanced session tuning, logging verbosity, tool hard-deny overrides, and
  low-level tool execution ceilings are not init-owned, but stay out of this
  config-command close unless explicitly promoted.

#### Task 1.1: Complete Channels provider-backed validation and canonical persistence

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `openspec/specs/netclaw-config-command/spec.md`, `openspec/specs/channel-audience-tui/spec.md`, `openspec/specs/netclaw-input-adapters/spec.md`
**Surface area:** UI, config, runtime contract
**Verification:** L3

Done when:

- [x] Slack channel names entered in config are resolved through Slack before
  persistence.
- [x] Slack `AllowedChannelIds` persists canonical Slack channel IDs (`C...` or
  `G...`) and never unresolved display names.
- [x] Slack channel audience keys are remapped to resolved channel IDs.
- [x] Discord channel IDs are checked through `IDiscordProbe.ResolveChannelIdsAsync`
  before save.
- [x] Mattermost channel IDs are checked through a Mattermost config-time probe
  before save.
- [x] Unresolved Slack, Discord, and Mattermost channel targets block save with
  visible errors.
- [x] Existing configured secrets can be used for validation without prompting
  on re-entry.
- [x] Tests cover Slack name -> ID resolution, Slack unresolved name rejection,
  Discord unresolved ID rejection, Mattermost unresolved ID rejection, and secret
  preservation.
- [x] Native smoke `./scripts/smoke/run-smoke.sh config-channels` passes with
  semantic assertions on canonical persisted values.
- [x] Docker POC image rebuild/relaunch was not used for this task's verification;
  native smoke provided the L3 gate.

#### Task 1.2: Finish generalized config leaf validation

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `openspec/specs/netclaw-config-command/spec.md`, `openspec/specs/section-editor-abstraction/spec.md`
**Surface area:** config, UI, cross-cutting
**Verification:** L3

Done when:

- [x] Every `netclaw config` leaf has typed structural validation before save.
- [x] Runtime/probe validation is run where the leaf writes values consumed by
  runtime startup, ACL, transport, tools, or daemon exposure.
- [x] Structurally invalid config is a hard block.
- [x] `Save anyway` exists only for transient runtime/probe failures, never for
  schema violations, missing required security fields, or unresolved canonical
  IDs.
- [x] Tests prove invalid path, URI, auth, binary, local-reference, and
  reachability failures where those concepts apply.
- [x] Smoke assertions check semantic preservation and canonical output, not
  byte-identical JSON.

#### Task 1.3: Complete `Security & Access` config area

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`, `docs/prd/PRD-002-gateway-security-envelope.md`
**Spec:** `openspec/specs/netclaw-config-command/spec.md`, `openspec/specs/security-posture-tui/spec.md`, `openspec/specs/netclaw-acl/spec.md`
**Surface area:** UI, config, security
**Verification:** L3

Done when:

- [x] `Security & Access` contains Security Posture, Enabled Features, Audience
  Profiles, and Exposure Mode.
- [x] Security Posture remains distinct from runtime Enabled Features and
  Audience Profiles.
- [x] Team/Public posture continues into Enabled Features; Personal posture does
  not force that continuation.
- [x] Audience Profiles expose only curated high-level controls: Tool Access
  (non-MCP), File Access, Incoming Attachments, Reset to posture default.
- [x] Reset to posture default resets the full underlying audience profile,
  including hidden MCP and approval settings.
- [x] MCP permissions route to `netclaw mcp permissions`; they are not recreated
  in this editor.
- [x] Tests cover round-trip, hidden-field reset semantics, and ACL consumer
  expectations.
- [x] Native config smoke covers at least one posture change and one audience
  profile reset with semantic assertions.

#### Human Review Checkpoint: Security & Access config editor

- [x] Completed 2026-06-01: human smoke passed in rebuilt
  `netclaw-config-poc-local` container at commit `547c2c3`; no `401
  Unauthorized` after enabling Reverse Proxy and entering MCP permissions.

Stop here after Task 1.3 is completed, verified, and committed. Do not continue
into Task 1.4 until a human has spot-checked the live `netclaw config` Security
& Access experience in a real terminal.

Human smoke focus:

- Security Posture reads clearly and continues to Enabled Features for Team and
  Public, but not for Personal.
- Audience Profiles expose only curated controls and route MCP grants to
  `netclaw mcp permissions`.
- Reset overrides visibly restores the posture baseline and the persisted JSON
  clears hidden MCP and approval overrides.
- If Reverse Proxy is enabled from this TUI session, immediately entering MCP
  permissions must not return `401 Unauthorized`; the local daemon client must
  use the bootstrap `DeviceToken` written by the exposure-mode save.
- Exposure Mode is visible from Security & Access, but deeper Exposure Mode
  behavior remains Task 1.4 work.

Human smoke finding 2026-06-01: enabling Reverse Proxy and then navigating into
MCP permissions in the same `netclaw config` process produced `401 Unauthorized`.
Treat this as a config/runtime credential refresh regression, not an acceptable
manual workaround. Regression coverage belongs with daemon-client authentication
tests because the config TUI reuses the same `DaemonApi` instance after exposure
mode writes a fresh bootstrap `DeviceToken`. Fixed by commit `547c2c37` and
confirmed by human retest in the rebuilt validation container.

#### Task 1.4: Complete Exposure Mode config leaf

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`, `docs/prd/PRD-002-gateway-security-envelope.md`
**Spec:** `docs/spec/SPEC-006-gateway-exposure-and-remote-access.md`, `openspec/specs/daemon-exposure/spec.md`, `openspec/specs/device-pairing/spec.md`
**Surface area:** UI, config, daemon exposure
**Verification:** L3

Done when:

- [ ] Explicit modes are Local, Reverse Proxy, Tailscale Serve, Tailscale
  Funnel, and Cloudflare Tunnel.
- [ ] `Daemon.ExposureMode` is the single active selector; no per-mode active
  flags are introduced.
- [ ] Inactive old values are preserved and ignored while inactive.
- [ ] Each non-local mode has a mode-specific dialog; Local requires no extra
  setup.
- [ ] First non-local enablement auto-pairs the current configuring client when
  no bootstrap/pairing state exists.
- [ ] Orphaned or mismatched bootstrap state blocks with actionable guidance to
  `netclaw doctor`, docs, and the tracked issue.
- [ ] Tests prove config merge semantics and daemon exposure consumer binding.
- [ ] Native config smoke covers at least one non-local mode and one return to
  Local.

#### Task 1.5: Complete Workspaces, Inbound Webhooks, and Browser Automation config areas

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`, `docs/prd/PRD-006-mcp-tool-integration.md`
**Spec:** `openspec/specs/netclaw-config-command/spec.md`, `openspec/specs/netclaw-mcp/spec.md`, `docs/spec/configuration.md`
**Surface area:** UI, config, workspaces, webhooks, MCP/browser tools
**Verification:** L3

Done when:

- [ ] Workspaces Directory is editable from `netclaw config`, validates as a
  local directory path, persists `Workspaces.Directory`, and preserves existing
  identity files.
- [ ] Tests prove `NetclawPaths.WorkspacesDirectory`, project discovery, and
  prompt/workspace consumers read the saved `Workspaces.Directory` value.
- [ ] Inbound Webhooks root entry routes to an implemented editor, not a
  placeholder.
- [ ] Inbound Webhooks editor controls `Webhooks.Enabled` and
  `Webhooks.ExecutionTimeoutSeconds`; route-file editing stays in
  `netclaw webhooks` / `~/.netclaw/config/webhooks/*.json` for this pass.
- [ ] Enabling inbound webhooks with no valid routes fails loudly through doctor
  or visible diagnostics; no dummy route is created silently.
- [ ] Browser Automation root entry routes to an implemented editor, not a
  placeholder.
- [ ] Browser Automation detects required local runtime pieces, refuses enablement
  when prerequisites are missing, and prints manual install guidance instead of
  shelling out from the TUI.
- [ ] Browser Automation persists/removes the canonical browser MCP server profile
  (`browser_playwright` or `browser_chrome_devtools`) using the same shape runtime
  MCP loading consumes.
- [ ] Browser Automation grants route to `netclaw mcp permissions`; raw MCP grant
  editing is not recreated in this editor.
- [ ] Native smoke covers at least one successful save path and one blocked or
  guidance-only path across these areas.

#### Task 1.6: Complete Skill Sources and Telemetry & Alerting config areas

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`, `docs/prd/PRD-006-mcp-tool-integration.md`
**Spec:** `openspec/specs/netclaw-config-command/spec.md`, `openspec/specs/netclaw-mcp/spec.md`
**Surface area:** UI, config, operations
**Verification:** L3

Done when:

- [ ] Skill Sources contains External Skills and Skill Feeds.
- [ ] Skill Source validation covers paths, URIs, auth, and reachability where
  relevant.
- [ ] Telemetry & Alerting contains Telemetry and Outbound Webhooks only in this
  pass.
- [ ] Delivery-policy tuning stays parked.
- [ ] Tests prove semantic round-trip, secret preservation, invalid URI/path
  rejection, and runtime consumer binding where applicable.
- [ ] Smoke tapes exercise both areas or document why an existing smoke covers
  the route.

#### Human Review Checkpoint: Complete config surface

Stop here after Tasks 1.4, 1.5, and 1.6 are completed, verified, and committed.
Do not continue into Task 1.7 until a human has spot-checked the live
`netclaw config` experience in the rebuilt validation container.

Human smoke focus:

- Exposure Mode can switch to a non-local mode and back to Local without stale
  runtime-active fields or missing local auth.
- Workspaces Directory, Inbound Webhooks, Browser Automation, Skill Sources,
  Telemetry, and Outbound Webhooks are implemented pages, not root-dashboard
  placeholders.
- Each page rejects structurally invalid values before persistence and preserves
  unrelated config/secrets.
- Browser Automation creates/removes the canonical browser MCP profile and routes
  grants to `netclaw mcp permissions`.
- Inbound Webhooks global enablement remains separate from route-file authoring;
  no dummy route is silently created.
- `./scripts/smoke/run-smoke.sh light` has passed or any local blocker is
  documented with evidence.

#### Task 1.7: Close the `netclaw config` OpenSpec change

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `openspec/changes/netclaw-config-command/tasks.md`
**Surface area:** planning, config
**Verification:** L3

Done when:

- [ ] `openspec/changes/netclaw-config-command/tasks.md` accurately reflects
  completed and incomplete implementation work.
- [ ] `openspec validate netclaw-config-command --type change` passes.
- [ ] `./scripts/smoke/run-smoke.sh light` passes on a clean runner or a local
  blocker is documented with evidence.
- [ ] `/opsx-verify netclaw-config-command` passes.
- [ ] Spec deltas are synced or the change remains explicitly active with only
  real unfinished tasks.

### Phase 2: Init Bootstrap Split

Purpose: keep first-run setup simple and move post-install editing to config.

#### Task 2.1: Simplify first-run `netclaw init`

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `docs/spec/SPEC-007-guided-onboarding.md`, `openspec/changes/simplify-netclaw-init/tasks.md`
**Surface area:** TUI, config bootstrap
**Verification:** L3

Done when:

- [ ] Planning and code remove all `netclaw init --force` assumptions.
- [ ] First-run init contains bootstrap-owned steps only.
- [ ] Posture values remain `Personal`, `Team`, `Public`.
- [ ] Identity remains init-owned.
- [ ] Post-flight messaging points users to `netclaw chat` and `netclaw config`.
- [ ] Init smoke `./scripts/smoke/run-smoke.sh init-wizard` passes.
- [ ] Full light smoke passes or local blockers are documented with evidence.

#### Task 2.2: Implement existing-install init menu and destructive reset flow

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `docs/spec/SPEC-007-guided-onboarding.md`, `openspec/changes/simplify-netclaw-init/tasks.md`
**Surface area:** TUI, config bootstrap, destructive actions
**Verification:** L3

Done when:

- [ ] Existing install shows exactly: `Redo identity setup`, `Open configuration
  editor`, `Start over from scratch`, `Cancel`.
- [ ] `Open configuration editor` routes to `netclaw config`.
- [ ] `Redo identity setup` routes only into init-owned identity flow.
- [ ] Start-over dialog shows exactly: `Reset setup only`, `Full reset`,
  `Cancel`.
- [ ] Both destructive actions require double confirmation.
- [ ] Tests cover refusal, menu routing, double confirmation, and preserved vs
  deleted files.
- [ ] Smoke coverage exercises existing-install menu and start-over cancellation.

#### Task 2.3: Close the `simplify-netclaw-init` OpenSpec change

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `openspec/changes/simplify-netclaw-init/tasks.md`
**Surface area:** planning, TUI
**Verification:** L3

Done when:

- [ ] `openspec validate simplify-netclaw-init --type change` passes.
- [ ] `/opsx-verify simplify-netclaw-init` passes.
- [ ] Init smoke and light smoke pass.
- [ ] Docs and skill guidance no longer describe stale init behavior.

### Phase 3: Runtime Adapter Contract Hardening

Purpose: prove each channel adapter accepts, denies, responds, and reports health
according to the same security envelope.

#### Task 3.1: Add adapter config-to-runtime contract tests

**PRD:** `docs/prd/PRD-009-input-adapters-and-unified-input.md`, `docs/prd/PRD-002-gateway-security-envelope.md`
**Spec:** `openspec/specs/netclaw-input-adapters/spec.md`, `openspec/specs/netclaw-slack-socket/spec.md`, `openspec/specs/netclaw-discord-socket/spec.md`, `openspec/specs/netclaw-acl/spec.md`
**Surface area:** runtime, config, ACL
**Verification:** L2

Done when:

- [ ] Slack, Discord, and Mattermost options bind from the config shape emitted
  by init/config editors.
- [ ] Allowed channel IDs and user IDs are consumed by runtime ACL in canonical
  provider form.
- [ ] Denied channel, denied user, allowed channel, and DM policy cases are
  covered per adapter.
- [ ] Misconfigured required tokens or server URLs fail closed for the affected
  channel without enabling permissive ingress.
- [ ] Tests name the producer and consumer for each contract.

#### Task 3.2: Add runtime reply-path smoke for local/demo adapters

**PRD:** `docs/prd/PRD-009-input-adapters-and-unified-input.md`
**Spec:** `openspec/specs/netclaw-input-adapters/spec.md`, `openspec/specs/netclaw-testing/spec.md`
**Surface area:** runtime, smoke
**Verification:** L4

Done when:

- [ ] Mattermost demo smoke posts a user message and proves the daemon routes it
  to a session and attempts a reply.
- [ ] Discord and Slack live smoke remain opt-in and credential-gated; absence
  of credentials skips with clear output, not failure.
- [ ] Runtime logs expose enough detail to diagnose allowed/denied/routed/reply
  states without leaking secrets.
- [ ] `TOOLING.md` documents the exact invocation and expected artifacts.

#### Task 3.3: Normalize channel diagnostics and doctor output

**PRD:** `docs/prd/PRD-003-operator-ux-ops-console.md`, `docs/prd/PRD-009-input-adapters-and-unified-input.md`
**Spec:** `docs/spec/SPEC-005-operator-ui-contract.md`, `openspec/specs/netclaw-operator-ui/spec.md`
**Surface area:** CLI, daemon diagnostics, operations
**Verification:** L2

Done when:

- [ ] `netclaw status` or doctor output distinguishes disconnected,
  misconfigured, denied-by-policy, and healthy per channel.
- [ ] Slack/Discord/Mattermost health outputs use consistent terms.
- [ ] Tests cover status mapping from runtime channel health to CLI/doctor
  display.
- [ ] Runbooks mention the deny and misconfiguration diagnostics operators
  should look for.

### Phase 4: Model Provider And Tool Execution Contracts

Purpose: keep model/provider/tool execution reliable and diagnosable across
provider differences.

#### Task 4.1: Harden provider/model config-to-runtime binding

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`, `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `docs/spec/SPEC-008-model-provider-abstraction.md`, `openspec/specs/netclaw-model-providers/spec.md`, `openspec/specs/netclaw-model-capabilities/spec.md`
**Surface area:** config, runtime, providers
**Verification:** L2

Done when:

- [ ] Provider and model editors emit config that runtime provider selection
  consumes without hidden defaults.
- [ ] Invalid provider IDs, missing model IDs, unsupported auth modes, and stale
  capability metadata fail visibly.
- [ ] Tests cover config editor output -> provider registry/model selection
  consumption.
- [ ] Eval suite is run if model/provider defaults or capability logic changes.

#### Task 4.2: Prove tool schema and permission contracts end-to-end

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`, `docs/prd/PRD-002-gateway-security-envelope.md`
**Spec:** `openspec/specs/netclaw-tools/spec.md`, `openspec/specs/netclaw-mcp/spec.md`, `openspec/specs/tool-call-metadata/spec.md`, `openspec/specs/mcp-schema-coercion/spec.md`
**Surface area:** tools, MCP, security
**Verification:** L2

Done when:

- [ ] Tool schemas generated for models match dispatcher expectations.
- [ ] MCP schema coercion has negative tests for invalid/coercion-impossible
  inputs.
- [ ] Tool approval and grant decisions are tested for allow, deny, prompt, and
  malformed metadata.
- [ ] No tool can bypass audience/profile policy because a field is missing or
  has a stale name.

#### Task 4.3: Keep streaming/progress execution contract coherent

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`, `docs/prd/PRD-006-mcp-tool-integration.md`
**Spec:** `openspec/changes/streaming-tool-call-execution/tasks.md`, `openspec/changes/progress-aware-turn-loop/tasks.md`, `openspec/specs/session-state-machine/spec.md`
**Surface area:** runtime, actors, tools
**Verification:** L2

Done when:

- [ ] Tool-call streaming, progress reporting, session phase transitions, and
  persistence snapshots agree on the same state names.
- [ ] Actor tests prove progress events survive normal tool completion,
  tool failure, cancellation, and session recovery.
- [ ] No turn loop can report success while a tool result is still pending.
- [ ] Logs/traces correlate model call, tool call, approval, and session turn.

### Phase 5: Memory, Identity, Scheduling, And Persistence Contracts

Purpose: ensure autonomous behavior survives restarts and carries the right
identity/context.

#### Task 5.1: Prove identity file and system prompt assembly contracts

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`, `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `openspec/specs/project-instructions/spec.md`, `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** identity, prompt assembly, evals
**Verification:** L2 plus eval suite

Done when:

- [ ] Init writes identity files in the exact paths prompt assembly reads.
- [ ] Prompt assembly rejects missing or malformed required identity assets
  visibly.
- [ ] Tests cover first-run, existing-install identity redo, missing file, and
  malformed file cases.
- [ ] Eval suite passes when identity grounding rules change.

#### Task 5.2: Prove memory recall and compaction persistence contracts

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**Spec:** `openspec/specs/netclaw-agent-memory/spec.md`, `openspec/specs/netclaw-session/spec.md`, `openspec/specs/thread-history-backfill/spec.md`
**Surface area:** persistence, memory, session actors
**Verification:** L2 plus eval suite

Done when:

- [ ] Memory recall inputs, persisted observations, compaction summaries, and
  prompt assembly use compatible serialization-safe types.
- [ ] Tests cover fresh session, resumed session, compacted session, and corrupt
  or missing memory state.
- [ ] Eval suite passes for memory pipeline and compaction changes.

#### Task 5.3: Prove scheduling delivery contracts

**PRD:** `docs/prd/PRD-008-scheduling-and-periodic-tasks.md`, `docs/prd/PRD-009-input-adapters-and-unified-input.md`
**Spec:** `openspec/specs/netclaw-scheduling/spec.md`, `openspec/specs/reminder-execution-history/spec.md`
**Surface area:** scheduling, actors, channel delivery
**Verification:** L2

Done when:

- [ ] Reminder targets resolve to channel gateways using canonical provider IDs.
- [ ] Current-session delivery routes through the existing session gateway chain
  without re-running inbound ACL checks.
- [ ] Future scheduled delivery uses policy appropriate for the stored target.
- [ ] Tests cover immediate reminder, periodic reminder, missed execution,
  failed delivery, restart recovery, and invalid target.
- [ ] `TimeProvider` is used for all scheduling time.

### Phase 6: Release Readiness And Packaging

Purpose: keep install, Docker, demo, and CI aligned with product behavior.

#### Task 6.1: Keep Docker image and install artifacts contract-tested

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`, `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `openspec/specs/daemon-container/spec.md`, `openspec/specs/manifest-signature-verification/spec.md`
**Surface area:** packaging, install, Docker
**Verification:** L3

Done when:

- [ ] Docker image contains matching CLI and daemon binaries from the same
  source build.
- [ ] Container default config path, health check, entrypoint, and self-update
  behavior match docs.
- [ ] Install smoke passes for Linux/macOS/Windows stand-in archives.
- [ ] Manifest signature verification negative paths are covered.
- [ ] Local POC rebuild instructions are documented and reproducible.

#### Task 6.2: Maintain demo AppHost as the local end-to-end proof

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`, `docs/prd/PRD-009-input-adapters-and-unified-input.md`
**Spec:** `openspec/changes/netclaw-demo-apphost/tasks.md`, `TOOLING.md`
**Surface area:** demo, runtime, smoke
**Verification:** L4

Done when:

- [ ] Demo AppHost boots Mattermost, Ollama, and daemon to healthy.
- [ ] Seeded Mattermost user can post into the configured channel.
- [ ] Daemon logs prove message routing into a session and model invocation.
- [ ] Slow CPU inference remains documented as latency caveat, not hidden as a
  failed wiring assertion.
- [ ] Opt-in demo integration test remains skipped by default and passes with
  `NETCLAW_RUN_DEMO_SMOKE=1` on a suitable Docker host.

## NEXT

NEXT tasks are important but not eligible for autonomous execution unless moved
to `NOW` by the user.

- Webhook service identity and inbound webhook route hardening beyond the config
  enablement/timeout editor.
- Subagent explicit model selection and parent-context alignment.
- GitHub Copilot provider refinements and VLLM capability strategy.
- Approval button label refinement and richer interactive approval UX.
- Config hot-reload beyond current startup/configure flows.
- Operator UX/Ops Console beyond CLI/TUI diagnostics.

## LATER

LATER tasks are product-direction items and should stay out of execution loops.

- Ambient monitoring workflows.
- Delegated coding task orchestration.
- Browser automation as a first-class feature beyond config-time MCP profile
  enablement.
- Split gateway/agent process architecture.
- Hosted SaaS / multi-tenant operator console.

## Required Session Closure Checklist

Before declaring any implementation session done, record the closure state in
the final response and, if a task remains incomplete, leave a concrete follow-up
in this plan.

- [ ] Which `IMPLEMENTATION_PLAN.md` task was worked.
- [ ] Producer/consumer contract identified.
- [ ] Positive behavior verified.
- [ ] Negative behavior verified.
- [ ] Runtime/smoke/eval validation completed or explicitly blocked.
- [ ] Docs/spec/skill updates completed or explicitly not applicable.
- [ ] Commands run and results reported.
- [ ] Worktree state reported.
