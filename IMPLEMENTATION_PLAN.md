# Netclaw Implementation Plan

Last updated: 2026-02-21
Mode: build

This file is RALPH-consumable.

Rules for loop execution:
- RALPH always picks the first task with unchecked `Done when` items.
- One iteration completes one task block.
- Task metadata must include PRD + OpenSpec references.
- Commits that complete a task must update this file and the referenced
  `openspec/changes/*/tasks.md` entries.

---

## Phase 0: Planning and Infrastructure Baseline (Completed)

### Task 0.1: Establish product planning baseline

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`, `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-gateway-security/spec.md`, `openspec/specs/netclaw-acl/spec.md`, `openspec/specs/netclaw-slack-socket/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** planning
**Verification:** L0

Done when:
- [x] PRDs and engineering specs are created and cross-referenced.
- [x] OpenSpec capabilities exist for core MVP behavior.
- [x] `openspec validate --all --no-interactive` passes.

### Task 0.2: Replace template scaffold with Netclaw projects

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-session/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** runtime scaffold
**Verification:** L1

Done when:
- [x] `Netclaw.slnx` exists and template `SampleSln.slnx` is removed.
- [x] `src/Akka.Agents` and `src/Netclaw.App` exist on .NET 10.
- [x] `dotnet build Netclaw.slnx` passes.

### Task 0.3: Import RALPH loop infrastructure with OpenSpec terminology

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-cli/spec.md`, `openspec/specs/netclaw-testing/spec.md`
**OpenSpec Changes:** `openspec/changes/design-ops-console-and-cli-v1/`, `openspec/changes/add-provider-smoke-and-ci-independence/`
**Surface area:** tooling/process
**Verification:** L0

Done when:
- [x] `ralph.sh` and `ralph-opencode.sh` are present and executable.
- [x] `.claude/skills/ralph-loop.md`, `.claude/skills/ralph-run-diagnostics.md`, and `.claude/skills/ralph-output-adversarial-review.md` exist locally.
- [x] RALPH skills/scripts are updated to reference OpenSpec artifacts and validation.
- [x] `.gitignore` includes `.ralph/`, `.planning/`, `.code-health/`, and local Claude settings ignore.

---

## Phase 1: Slack Session Vertical Slice

### Task 1.1: Implement framework protocol and persistence-safe message envelopes

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-session/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** actor framework
**Verification:** L2

Done when:
- [ ] `Commands`, `Events`, and `Broadcasts` are implemented with concrete types.
- [ ] Framework-owned serializable chat message type is implemented (no direct persistence of `Microsoft.Extensions.AI` types).
- [ ] Session entity key semantics `{channelId}/{threadTs}` are encoded in protocol contracts.
- [ ] Integration tests verify event serialization round-trip.

### Task 1.2: Implement `LlmSessionActor` persistence and turn loop

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-slack-socket/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** actor runtime
**Verification:** L2

Done when:
- [ ] Actor recovers state from journal/snapshot before handling new turns.
- [ ] Turn processing persists turn events and emits turn broadcasts.
- [ ] Snapshot strategy and compaction trigger plumbing are implemented.
- [ ] Integration tests prove restart recovery preserves context.

### Task 1.3: Implement session parent and entity routing

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-session/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** actor runtime
**Verification:** L2

Done when:
- [ ] `LlmAgentParentActor` wraps `GenericChildPerEntityParent` behavior.
- [ ] Session extraction routes same thread messages to the same child actor.
- [ ] Parent actor tests verify entity lifecycle and message routing behavior.

### Task 1.4: Wire Slack Socket Mode vertical slice

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-slack-socket/spec.md`, `openspec/specs/netclaw-session/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** integration
**Verification:** L2

Done when:
- [ ] Slack Socket Mode adapter receives inbound events and dispatches actor commands.
- [ ] Reply broadcast is posted into the originating Slack thread.
- [ ] End-to-end local test proves message -> reply loop.

---

## Phase 2: Security Envelope and ACL Enforcement

### Task 2.1: Implement ACL configuration loader and evaluator

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-acl/spec.md`, `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** security
**Verification:** L2

Done when:
- [ ] ACL parser supports channel rules, sender allowlists, and mention/ambient mode.
- [ ] Default deny behavior is enforced when no explicit allow exists.
- [ ] Invalid ACL blocks startup with actionable diagnostics.
- [ ] Policy decision tests cover allow/deny reason codes.

### Task 2.2: Enforce tool and MCP policy gates

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`, `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-acl/spec.md`, `openspec/specs/netclaw-mcp/spec.md`
**OpenSpec Changes:** `openspec/changes/add-mcp-support-v1/`
**Surface area:** security/integration
**Verification:** L2

Done when:
- [ ] Tool and MCP invocations require explicit grants.
- [ ] Denied calls return policy reason codes and audit records.
- [ ] Integration tests verify denial path for missing grants.

### Task 2.3: Implement exposure modes and privileged approval checks

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** security/ops
**Verification:** L2

Done when:
- [ ] `local`, `tailscale-serve`, `tailscale-funnel`, and `cloudflare-tunnel` modes are validated.
- [ ] Public modes require configured auth policy and fail validation otherwise.
- [ ] `gateway doctor/status` surfaces exposure and approval state.

---

## Phase 3: Guided Onboarding, Providers, MCP, and Testing

### Task 3.1: Implement guided onboarding CLI flow

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-cli/spec.md`, `openspec/specs/netclaw-onboarding/spec.md`
**OpenSpec Changes:** `openspec/changes/add-guided-onboarding-and-provider-strategy/`
**Surface area:** CLI
**Verification:** L1

Done when:
- [ ] `netclaw init` implements stepwise onboarding with resume support.
- [ ] Onboarding captures Slack Socket Mode configuration and ACL bootstrap.
- [ ] Onboarding output includes final readiness summary and next command.

### Task 3.2: Implement provider abstraction and local smoke profile defaults

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-model-providers/spec.md`, `openspec/specs/netclaw-testing/spec.md`
**OpenSpec Changes:** `openspec/changes/add-guided-onboarding-and-provider-strategy/`, `openspec/changes/add-provider-smoke-and-ci-independence/`
**Surface area:** provider integration
**Verification:** L2

Done when:
- [ ] Provider abstraction supports OpenRouter, OpenAI, Anthropic, and Ollama profiles.
- [ ] Local smoke defaults target `http://big-gpu:11434` with `qwen3:30b` (`qwen3:14b` fallback).
- [ ] `netclaw test smoke --provider ollama` reports actionable pass/fail diagnostics.

### Task 3.3: Implement MCP server registry and validation commands

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-mcp/spec.md`, `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Changes:** `openspec/changes/add-mcp-support-v1/`
**Surface area:** integration/CLI
**Verification:** L2

Done when:
- [ ] MCP profile configuration supports named servers with enable/disable control.
- [ ] `netclaw mcp list|validate|test` commands are implemented.
- [ ] Runtime degrades gracefully when MCP server is unavailable.

### Task 3.4: Implement CI-safe testing split

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`, `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-testing/spec.md`
**OpenSpec Changes:** `openspec/changes/add-provider-smoke-and-ci-independence/`
**Surface area:** testing/CI
**Verification:** L2

Done when:
- [ ] CI-required tests run without live provider credentials.
- [ ] Live provider smoke tests are opt-in and excluded from required CI jobs.
- [ ] Test docs and pipeline config reflect category split.

---

## Phase 4: Ops Console and Diagnostics Surface

### Task 4.1: Implement management API endpoints for UI contracts

**PRD:** `docs/prd/PRD-003-operator-ux-ops-console.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-operator-ui/spec.md`, `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Changes:** `openspec/changes/design-ops-console-and-cli-v1/`
**Surface area:** web API
**Verification:** L2

Done when:
- [ ] API endpoints exist for overview, sessions, policy decisions, security, and MCP health.
- [ ] Response shapes align with `SPEC-005-operator-ui-contract.md`.
- [ ] Integration tests cover key API contract responses.

### Task 4.2: Implement minimal ops console UI shell

**PRD:** `docs/prd/PRD-003-operator-ux-ops-console.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-operator-ui/spec.md`
**OpenSpec Changes:** `openspec/changes/design-ops-console-and-cli-v1/`
**Surface area:** UI
**Verification:** L3

Done when:
- [ ] Routes `/overview`, `/sessions`, `/policy`, `/security`, `/diagnostics`, and `/tools` exist.
- [ ] UI displays health, policy, and MCP summary data from management APIs.
- [ ] L3 evidence includes screenshots and console-clean checks.

---

## Phase 5: Host Acceptance and OpenSpec Lifecycle

### Task 5.1: Validate pi1 MVP acceptance flow

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-gateway-security/spec.md`, `openspec/specs/netclaw-mcp/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`, `openspec/changes/add-mcp-support-v1/`
**Surface area:** end-to-end
**Verification:** L4

Done when:
- [ ] Slack in-thread replies work on `pi1` with session continuity across restart.
- [ ] Compaction preserves working context for long threads.
- [ ] ACL/exposure policy denial paths are verified on host.
- [ ] Local smoke test against `big-gpu` succeeds when invoked.

### Task 5.2: Archive completed OpenSpec changes and prep release notes

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec Capabilities:** `openspec/specs/README.md`
**OpenSpec Changes:** `openspec/changes/*`
**Surface area:** release/process
**Verification:** L0

Done when:
- [ ] Completed changes are archived with `openspec archive <change-name>`.
- [ ] Main capability specs reflect implemented behavior.
- [ ] `RELEASE_NOTES.md` captures user-facing MVP behavior delivered.
