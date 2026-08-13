# Netclaw Implementation Plan

Last updated: 2026-08-11

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

### Priority: Keep MCP HTTP Protocol Fallback Deterministic

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**Spec:** `openspec/specs/netclaw-mcp/spec.md`
**Surface area:** MCP HTTP transport, daemon connections, CLI probes
**Verification:** L1 plus the existing HTTP MCP smoke tests

The MCP SDK can retain its discovery protocol version when probe cancellation
selects the initialize fallback. Netclaw must not send that stale version in an
initialize request.

Done when:

- [x] Daemon connections and CLI probes remove a retained protocol-version
  header only from the initialize request.
- [x] Discovery and established-session requests keep their protocol-version
  header.
- [x] Tests prove the header correction and preserve unrelated headers.

### Priority: Preserve The Daemon Working Directory

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**Specs:** `openspec/specs/netclaw-tools/spec.md`, `openspec/specs/tool-approval-gates/spec.md`
**Surface area:** daemon lifecycle, path normalization, shell authorization
**Verification:** L1 plus a live daemon restart

The daemon process directory must survive routine system temporary-directory
cleanup. Absolute path validation must not depend on that process directory.

Done when:

- [x] The daemon uses a durable runtime directory below the Netclaw home.
- [x] Absolute path normalization does not read the process working directory.
- [x] Focused tests and the full repository quality gates pass.
- [ ] An installed daemon restart confirms the live process uses the durable directory.

### Priority: Reduce Shell Approval Fatigue

**PRDs:** `docs/prd/PRD-002-gateway-security-envelope.md`, `docs/prd/PRD-006-mcp-tool-integration.md`
**Spec:** `openspec/specs/tool-approval-gates/spec.md`
**Surface area:** shell authorization, approval matching, security corpus
**Verification:** L2

The user promoted this work into `NOW`. The work must reduce repeat prompts
without allowing an incomplete or unknown shell form.

Done when:

- [x] The sanitized v0.26.0-beta.3 approval window has 18 classified cases in
  the linked ShellSyntaxTree and Netclaw OpenSpec changes.
- [x] `openspec/changes/structure-shell-approval-policy/` defines the typed
  coordinator and actor protocol, per-candidate coverage, real and intent
  scopes, token-boundary grants, reviewed policy catalog, bounded trace,
  migration, and validation tasks.
- [x] The maintainer approves the ShellSyntaxTree 0.3.1 API names and the
  use of separate authored-source facts for approval matching before
  implementation.
- [x] The maintainer approves whether simple v2 grants gain token-prefix
  authority during schema-3 migration or remain exact until re-approved.
- [x] Approval-store schema 3 uses closed shell token-prefix, shell legacy-exact,
  and non-shell exact forms. Version-2 conversion keeps prior shell authority
  exact, creates a byte-identical backup, and fails closed on invalid data or
  storage errors.
- [x] New reusable shell grants use ShellSyntaxTree token facts with explicit
  Bash or PowerShell identity. Distinct command occurrences remain visible
  through actor checks, one-time keys, persisted events, and subagent prompts.
- [x] CLI and TUI list and revoke typed phrases with unambiguous labels. The
  native approvals tape proves version-2 conversion, non-shell label display,
  and revoke behavior against a published binary.
- [ ] The policy pipeline replaces the shell branches in `ToolAccessPolicy`
  and `ShellApprovalMatcher`; any retained legacy scan is deny-only and cannot
  authorize, create candidates, or widen scope.
- [ ] The bundled safe catalog removes every executable whose accepted
  arguments can write, delete, execute code, or mutate a remote service through
  executable argv interpretation. Redirect, parser-owned path/provider, and
  unknown shell-expansion checks remain separate.
- [ ] The `git ls-tree` production special case is removed. Token-boundary
  policy match handles longer parser phrases without private command grammar.
- [x] A synthetic workload corpus covers ordinary search, read, pipeline,
  redirect, and file-change commands without production command text.
- [x] A safe pipeline stage can compose with a stored grant for each stage that
  still requires approval.
- [x] A prompt excludes a safe stage from the approval candidates that the user
  can persist.
- [x] A prompt excludes candidates that existing session or persistent grants
  already cover, while it preserves exact directory-scoped occurrences.
- [x] A one-time retry is bound to the exact prompted candidate set, including
  each effective directory, across live, sub-agent, and redrive paths.
- [x] External paths, mismatched grants, dynamic syntax, and hard-deny rules
  keep their strict behavior.
- [x] Directory operands preserve dotted directory names without weakening the
  external-path or symlink checks.
- [x] The policy normalizes a variable `git ls-tree` tree operand to the
  reviewed read-only verb. Other Git subcommands keep exact parser output.
- [x] Tool schemas and always-loaded guidance distinguish a persistent project
  root from one-command `WorkingDirectory` scope, prevent redundant project
  switches, and preserve `cd` when directory mutation is the requested shell
  behavior.
- [x] Reviewed-safe shell work beneath an undeclared cwd returns a
  `set_working_directory` correction to the agent before any user prompt. The
  original tool call remains unchanged; the shared directory policy must accept
  the exact non-temp cwd, while unsafe phrases, outside paths, Public sessions,
  and unavailable scope tools retain normal approval behavior.
- [x] Bounded non-path `IntegerRange` and `Concatenation` data do not make a
  complete shell command complex. Unknown values, identities, paths, and
  redirects stay strict.
- [x] Sanitized behavioral eval cases cover early project declaration,
  one-command typed scope, failed-path recovery, and deliberate inline `cd`.
- [x] The session-scratch model-guidance eval passed 4/5 against the configured
  `deepseek-v4-flash-dspark` endpoint. This measures headless path preference;
  deterministic actor tests own interactive correction and approval proof.
- [x] Explicit `WorkingDirectory=/tmp` and deliberate inline `cd /tmp` evals
  remain in the corpus so a platform-temp requirement is not rewritten.
- [x] Eligible interactive Personal shell work at the shared platform-temp root
  receives a typed session-scratch correction before parent or subagent
  approval. The original call remains in history. One exact later retry offers
  only Once or Deny and creates no reusable temp authority. Headless behavior,
  Team/Public denial, hard-deny rules, dynamic syntax, protected paths, and
  native PowerShell causal scope stay strict.
- [ ] Define automated session-directory cleanup in a separate OpenSpec before
  adding retention or deletion behavior.
- [ ] A constrained executable grammar proves any future safe `sed` form. The
  `-n` option alone is not proof because a `sed` program can write files or
  execute commands.
- [x] Netclaw consumes ShellSyntaxTree 0.3.0-alpha command occurrences and
  explicit Bash redirect facts for the existing grammar.
- [x] Netclaw consumes ShellSyntaxTree `0.3.0` through its corrected
  closed analysis API. The consumer uses joined arguments, value-domain type
  patterns, redirect alternatives, and redirect-source alternatives. The
  unchanged 225-test Bash, PowerShell 7, and Windows PowerShell 5.1 approval
  matrix passes locally.
- [x] Netclaw resolves ShellSyntaxTree `0.3.2` for the separate authored-source
  and path-shape facts introduced in 0.3.1. This store-v3 slice preserves those parser token facts
  without executable-private command rules; later parent tasks consume the new
  value-domain facts in the coordinator.
- [ ] Netclaw consumes public ShellSyntaxTree `0.3.3` for the parser-owned
  authored filesystem domain. Local code accepts only `Exact` and `FiniteSet`.
  It checks each value through path policy and keeps unsafe transforms strict.
  The Release build and all 7,138 runnable tests pass. The suite reports 15
  expected platform or opt-in skips. Adversarial review and CI remain before
  completion.
- [x] The expanded 247-test matrix covers command-substitution and PowerShell
  execution-region behavior. Known command-owned regions reuse independently
  matched host and body grants after Netclaw accounts for the parsed body.
  Unknown receivers and incomplete region facts remain prompt-only.
- [x] Unknown occurrences, cwd facts, wrappers, and redirects stay prompt-only.
  Static descriptor redirects no longer appear dynamic.
- [x] The resolved POSIX `/dev/null` device does not create an approval
  directory after host symlink checks. Other device paths and dynamic redirect
  targets stay strict.
- [x] Netclaw consumes ShellSyntaxTree `0.3.0-alpha.1` and promotes Bash
  command-resolution mutation and reserved execution forms into the strict
  181-case review matrix.
- [x] ShellSyntaxTree `0.3.0-alpha.2` introduced one temporary POSIX PowerShell
  child wrapper. Native-host activation removed that transitional consumer
  behavior: Bash treats `pwsh` as an external command, and only a native
  PowerShell host uses `PwshParser`.
- [x] The shell approval review table separates Bash, PowerShell 7, and Windows
  PowerShell 5.1 rows. Cross-language payloads remain ordinary external-command
  arguments; same-language static children use parser-returned occurrences.
- [x] A constrained stdin grammar allows a complete literal heredoc or bounded
  here string only for argument-free `cat`. Unknown data, expanding heredocs,
  arguments, wrappers, interpreters, and stored grants stay strict.
- [x] Netclaw interprets bounded non-path loop arguments through
  ShellSyntaxTree 0.3.2's separate authored-source projection for approval
  analysis. Effective runtime values retain priority, and path-bearing loop
  arguments remain strict.
  - [x] The approval matrix pins inherited and same-language child loops as
    complex under the canonical unknown-state contract for Bash, PowerShell 7,
    and Windows PowerShell 5.1. It also proves that a stored command grant
    cannot cover an unproved loop-dependent argument.
  - [x] Netclaw adopts bounded non-path `AuthoredValue`, status concatenation,
    and the typed consumer boundary. Positive authored path evidence remains a
    separate strict-policy task.

### Priority: Use Native PowerShell on Windows

**PRDs:** `docs/prd/PRD-001-netclaw-mvp.md`, `docs/prd/PRD-002-gateway-security-envelope.md`, `docs/prd/PRD-006-mcp-tool-integration.md`
**Specs:** `openspec/changes/archive/2026-08-10-native-windows-powershell-host/`
**Surface area:** shell execution, parsing, approval policy, model context
**Verification:** L2 plus native Windows L3

This work replaces `cmd.exe` with a native PowerShell host on Windows. Netclaw
prefers a compatible PowerShell 7.6 host and falls back to Windows PowerShell
5.1. It keeps Bash and PowerShell as separate host languages.

The additive foundation pins ShellSyntaxTree `0.3.0-alpha.5` and defines the
immutable environment, strict host probe, and process arguments. Runtime
activation now routes execution, policy, approval, background jobs, and model
context through the same resolved environment. PR #1848 auto-merged after
adversarial review and green native Windows CI. The canonical specifications
now contain the delivered contract. The OpenSpec change is archived.

Local validation on 2026-08-10 passed restore, the zero-warning Release build,
the full solution test suite, changed-file format verification, headers,
Slopwatch, `git diff --check`, and strict OpenSpec validation. The shell-platform
behavioral evaluation was unavailable because the required
`NETCLAW_EVAL_PROVIDER_TYPE`, `NETCLAW_EVAL_PROVIDER_ENDPOINT`, and
`NETCLAW_EVAL_MODEL_ID` settings were absent. This result is blocked evidence,
not an evaluation pass.

Native Windows workflow run `31381580072` passed the zero-warning Release build,
the complete security, actor, and daemon test suites, package staging, and CLI
smoke tests. The production resolver and deterministic dialect matrix cover
PowerShell 7.6 and Windows PowerShell 5.1. The Windows suite also executed the
explicit Windows PowerShell 5.1 host test.

The final OpenSpec verification mapped all 7 requirements and 24 scenarios to
runtime code and named tests. The focused verification passed 320 security
tests, 29 daemon tests, and 315 actor tests. One Windows-only actor test skipped
locally and passed in native Windows workflow run `31381580072`. Strict
validation passed all 78 OpenSpec items with no failures.

Done when:

- [x] One immutable shell environment selects the absolute executable path,
  grammar, path style, process arguments, and PowerShell dialect for the daemon
  lifetime.
- [x] Windows selects `pwsh.exe` only for versions from 7.6.4 through 7.6.x. It
  falls back to `powershell.exe` 5.1 and fails clearly if neither host matches.
- [x] Execution, parsing, hard deny, approval matching, prompt display, and
  model context use the same selected environment.
- [x] Bash treats `pwsh` as an external command. PowerShell treats `bash` as an
  external command. Only same-language child hosts can recurse.
- [x] Unknown or incomplete facts cannot produce a stored approval candidate
  or a safe-verb pass. Stored approval cannot bypass hard deny.
- [x] Personal sessions state the platform, executable, grammar, and dialect,
  including sessions that have no project directory.
- [x] Native Windows tests cover PowerShell 7.6 and Windows PowerShell 5.1.
  The security review table covers direct, child, retry, and background paths.
- [x] Consumer guidance and canonical specs match the delivered behavior. The
  OpenSpec change passes verification, is synchronized, and is archived.

### Priority: Simplify Tool Execution Context Architecture

**PRDs:** `docs/prd/PRD-001-netclaw-mvp.md`, `docs/prd/PRD-002-gateway-security-envelope.md`, `docs/prd/PRD-006-mcp-tool-integration.md`, `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**Specs:** `openspec/changes/archive/2026-07-15-simplify-tool-execution-context/`
**Surface area:** tool execution, session actors, subagents, working context
**Verification:** L2 plus behavioral evals

Deliver three sequential, independently reviewed PRs from the active OpenSpec
change. The series replaces nullable and context-free execution APIs with
required immutable scopes and semantic value objects, composes the session
pipeline with behavior-preserving dependency modeling, and gives child runs independent
working state with gated asynchronous Git enrichment.

Done when:

- [x] Stage 1 lands required run scopes, per-call isolation, and non-null
  security/authority dependencies without compatibility shims.
- [x] Stage 2 lands the composed pipeline without changing existing background,
  fallback, authorization, approval, MCP, or model-visible behavior.
- [x] Stage 3 lands child fork/delta semantics and Git inspection only for
  non-Public runs with a declared Git project.
- [x] Each stage passes review, CI, post-merge fresh-worktree verification, and
  repository quality gates. Stage 3 eval execution was attempted but explicitly
  blocked because the required `NETCLAW_EVAL_*` provider environment was absent;
  no model-facing tool schema or prompt behavior changed in that stage.
- [x] OpenSpec deltas are verified, synced, and archived after the final merge.

Durable execution details and checkbox state live in
`openspec/changes/archive/2026-07-15-simplify-tool-execution-context/tasks.md`. Per-run evidence
lives in `.ralph/runs/`; Git commits and PR state remain the recovery source of
truth across context compaction.

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

- [x] Explicit modes are Local, Reverse Proxy, Tailscale Serve, Tailscale
  Funnel, and Cloudflare Tunnel.
- [x] `Daemon.ExposureMode` is the single active selector; no per-mode active
  flags are introduced.
- [x] Inactive old values are preserved and ignored while inactive.
- [x] Each non-local mode has a mode-specific dialog; Local requires no extra
  setup.
- [x] First non-local enablement auto-pairs the current configuring client when
  no bootstrap/pairing state exists.
- [x] Orphaned or mismatched bootstrap state blocks with actionable guidance to
  `netclaw doctor`, docs, and the tracked issue.
- [x] Tests prove config merge semantics and daemon exposure consumer binding.
- [x] Native config smoke covers at least one non-local mode and one return to
  Local.

#### Task 1.5: Complete Workspaces, Inbound Webhooks, and Browser Automation config areas

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`, `docs/prd/PRD-006-mcp-tool-integration.md`
**Spec:** `openspec/specs/netclaw-config-command/spec.md`, `openspec/specs/netclaw-mcp/spec.md`, `docs/spec/configuration.md`
**Surface area:** UI, config, workspaces, webhooks, MCP/browser tools
**Verification:** L3

Done when:

- [x] Workspaces Directory is editable from `netclaw config`, validates as a
  local directory path, persists `Workspaces.Directory`, and preserves existing
  identity files.
- [x] Tests prove `NetclawPaths.WorkspacesDirectory`, project discovery, and
  prompt/workspace consumers read the saved `Workspaces.Directory` value.
- [x] Inbound Webhooks root entry routes to an implemented editor, not a
  placeholder.
- [x] Inbound Webhooks editor controls `Webhooks.Enabled` and
  `Webhooks.ExecutionTimeoutSeconds`; route-file editing stays in
  `netclaw webhooks` / `~/.netclaw/config/webhooks/*.json` for this pass.
- [x] Enabling inbound webhooks with no valid routes fails loudly through doctor
  or visible diagnostics; no dummy route is created silently.
- [x] Browser Automation root entry routes to an implemented editor, not a
  placeholder.
- [x] Browser Automation detects required local runtime pieces, refuses enablement
  when prerequisites are missing, and prints manual install guidance instead of
  shelling out from the TUI.
- [x] Browser Automation persists/removes the canonical browser MCP server profile
  (`browser_playwright` or `browser_chrome_devtools`) using the same shape runtime
  MCP loading consumes.
- [x] Browser Automation grants route to `netclaw mcp permissions`; raw MCP grant
  editing is not recreated in this editor.
- [x] Native smoke covers at least one successful save path and one blocked or
  guidance-only path across these areas.

#### Task 1.6: Complete Skill Sources and Telemetry & Alerting config areas

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`, `docs/prd/PRD-006-mcp-tool-integration.md`
**Spec:** `openspec/specs/netclaw-config-command/spec.md`, `openspec/specs/netclaw-mcp/spec.md`
**Surface area:** UI, config, operations
**Verification:** L3

Done when:

- [x] Skill Sources contains External Skills and Skill Feeds.
- [x] Skill Source validation covers paths, URIs, auth, and reachability where
  relevant.
- [x] Telemetry & Alerting contains Telemetry and Outbound Webhooks only in this
  pass.
- [x] Delivery-policy tuning stays parked.
- [x] Tests prove semantic round-trip, secret preservation, invalid URI/path
  rejection, and runtime consumer binding where applicable.
- [x] Smoke tapes exercise both areas or document why an existing smoke covers
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
**Spec:** `docs/spec/SPEC-016-tool-liveness-and-stall-detection.md`, `openspec/changes/streaming-tool-call-execution/tasks.md`, `openspec/specs/session-state-machine/spec.md`
**Surface area:** runtime, actors, tools
**Verification:** L2

Done when:

- [ ] Tool-call streaming, progress reporting, session phase transitions, and
  persistence snapshots agree on the same state names.
- [ ] Tool liveness is classified as `Opaque` or `SelfMonitoring`; generated
  tools default to `Opaque`, and `spawn_agent` is explicitly `SelfMonitoring`.
- [ ] Opaque tools use one wall-clock budget; streamed stdout/stderr or other
  output does not reset the budget.
- [ ] Self-monitoring tools use only a parent first-item startup guard after
  which the child/tool-owned watchdog reports terminal success or failure.
- [ ] Actor tests prove progress events survive normal tool completion,
  tool failure, cancellation, and session recovery.
- [ ] Actor tests prove a quiet-but-healthy sub-agent is not killed by the parent
  `Session.ToolExecutionTimeoutSeconds`, while child prefill/no-progress stalls
  still produce terminal failed `spawn_agent` results.
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
