# PRD-004: CLI Onboarding and Configuration

## Status

- State: Draft for execution
- Owner: Netclaw engineering
- Date: 2026-02-21
- Depends on: `PRD-001`, `PRD-002`, `PRD-003`

## Goal

Provide a first-class operator CLI to bootstrap, validate, and troubleshoot
Netclaw without manual file hunting.

## Product Outcome

An owner can go from empty config to safe runtime startup and ongoing diagnostics
using CLI commands and guided output.

## UX Direction

- onboarding-first, safe defaults
- explicit security confirmations before risky enablement
- step-by-step provider and Slack setup with validation at each step

## Command Surface (MVP)

- `netclaw init`
- `netclaw config show|validate`
- `netclaw acl validate|test|explain`
- `netclaw gateway status|doctor|pair`
- `netclaw session inspect|compact`
- `netclaw prompt show|validate`
- `netclaw tools list|policy`
- `netclaw mcp list|validate|test`
- `netclaw test smoke [--provider ollama]`

## Requirements

### CLI-001 Onboarding

`init` creates baseline config and highlights required secrets and policy items.

### CLI-001A Guided Setup Wizard

`netclaw init` SHALL support an interactive guided onboarding flow that:

1. captures runtime profile (local only vs remote managed)
2. configures Slack Socket Mode credentials
3. configures model provider (OpenRouter default)
4. configures MCP server profiles (optional step, explicit enable)
5. scaffolds ACL in default-deny mode
6. runs final validation and prints next-step run commands

### CLI-002 Validation

`config validate` and `acl validate` provide structured errors with file path and
property location.

### CLI-003 Explainability

`acl explain` and `acl test` show effective policy decisions for sample inputs.

### CLI-004 Runtime Diagnostics

`gateway status` and `gateway doctor` summarize connectivity, persistence, and
policy health.

### CLI-005 Session Operations

`session inspect` exposes current state, last activity, and compaction metadata.

### CLI-006 Safe Defaults

Commands default to read-only behavior unless explicit write/apply flags are
provided.

### CLI-007 Onboarding Resume

The onboarding flow SHALL be resumable and indicate which setup steps are
completed, pending, or invalid.

## UX Requirements

- human-readable output by default, machine-friendly JSON opt-in
- explicit exit codes for automation
- no hidden side effects for diagnostic commands

## Acceptance Criteria

1. CLI spec covers onboarding, validation, policy diagnostics, and session ops.
2. Every high-risk command has confirmation or explicit `--yes` semantics.
3. Error output includes remediation guidance.
4. Fresh install reaches a runnable baseline in one guided flow.
