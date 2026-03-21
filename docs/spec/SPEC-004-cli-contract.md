# SPEC-004: CLI Contract

Source PRDs: `PRD-004`, `PRD-002`

## Purpose

Define the command-line contract for operator onboarding, configuration,
security diagnostics, and session operations.

## Command Families

### 1) Initialization

- `netclaw init`

Behavior:

- creates baseline config files if missing
- generates ACL skeleton in default-deny mode
- prints required environment variable checklist
- supports interactive guided setup mode by default
- supports non-interactive mode via explicit flags for automation

Guided setup sequence:

1. choose runtime profile (`local` / `remote`)
2. configure Slack Socket Mode tokens
3. configure model provider (default: OpenRouter)
4. scaffold ACL policy and owner allowlist
5. validate configuration and print startup command

### 2) Configuration

- `netclaw config show [--format text|json]`
- `netclaw config validate [--strict]`

Behavior:

- structured validation with property path and remediation hints
- non-zero exit code on validation failure

### 3) ACL and Policy

- `netclaw acl validate`
- `netclaw acl test --channel <id> --sender <id> [--mentioned true|false]`
- `netclaw acl explain --channel <id> --sender <id> [--tool <name>]`

Behavior:

- produces effective decision and reasons
- includes deny reason codes suitable for automation

### 4) Gateway Diagnostics

- `netclaw gateway status`
- `netclaw gateway doctor`
- `netclaw gateway pair [--approve <code>]`

Behavior:

- status summarizes Slack connectivity, persistence reachability, and policy mode
- doctor emits actionable diagnostics in priority order

### 5) Session Operations

- `netclaw session inspect --session <channel/threadTs>`
- `netclaw session compact --session <channel/threadTs> [--dry-run]`

Behavior:

- inspect is read-only
- compact requires explicit confirmation unless `--yes` is supplied

### 6) Prompt and Tools

- `netclaw prompt show`
- `netclaw prompt validate`
- `netclaw tools list`
- `netclaw tools policy --tool <name>`
- `netclaw mcp list`
- `netclaw mcp validate [--server <name>]`
- `netclaw mcp test --server <name> --tool <name>`
- `netclaw test smoke [--provider ollama]`

Behavior:

- prompt validation checks required opening/zero clause sections
- tools policy command reports effective grant state
- `netclaw mcp list` reports daemon-backed per-server runtime status and discovered tools
- `netclaw doctor` may include daemon-backed MCP auth/connectivity truth when available, and must label offline-only OAuth checks as non-authoritative
- smoke test command runs optional live integration checks outside CI-required
  test suite

## Output and Exit Codes

- default output: human readable text
- optional output: JSON for automation
- exit code `0` for success
- exit code `1` for validation, policy, or runtime failures
- exit code `2` for usage and argument errors

## Safety Rules

- read-only default for all inspection commands
- mutating commands require explicit confirmation or `--yes`
- no command may silently broaden exposure policy

## Onboarding State Persistence

- onboarding writes progress markers to config metadata
- `netclaw init --resume` continues incomplete onboarding
- `netclaw init --reset` restarts onboarding after explicit confirmation
