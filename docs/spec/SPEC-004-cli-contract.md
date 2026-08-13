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

### 4) Diagnostics

- `netclaw status`
- `netclaw doctor`

Behavior:

- status summarizes connector health, persistence reachability, active shell mode,
  and runtime policy state
- doctor emits actionable diagnostics in priority order, including strict-default
  trust-policy checks, unsafe audience-profile combinations, and sandbox-shell
  readiness when applicable

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
- tools policy command reports effective grant state and the resolved
  audience-profile scope when policy limits apply
- `netclaw mcp list` reports daemon-backed per-server runtime status and discovered tools
- `netclaw doctor` may include daemon-backed MCP auth/connectivity truth when available, and must label offline-only OAuth checks as non-authoritative
- smoke test command runs optional live integration checks outside CI-required
  test suite

### 7) Interactive Chat

- `netclaw chat [--session <id>]`

Behavior:

- starts a Termina application with `Inline` presentation
- selects `NativeTerminal` scroll input
- leaves settled output in the primary terminal buffer
- exits any full-screen session picker before chat starts
- fails visibly when inline mode cannot start
- never selects full-screen chat as a silent fallback

Setup, config, provider, model, and session picker applications retain
`FullScreen` presentation.

The chat composer uses bare `Enter` for submit and `Shift+Enter` for a newline.
A model call does not disable or hide the composer. Later prompts enter the
session actor queue while the current turn runs. The actor retains all accepted
prompts in FIFO order and includes them in one follow-up model call. The client
does not start one turn for each queued prompt. The live region shows assistant
text as each stream delta arrives.
A pending approval owns input before the composer. `Ctrl+O` changes approval
detail without a decision.

## Output and Exit Codes

- default output: human readable text
- optional output: JSON for automation
- exit code `0` for success
- exit code `1` for validation, policy, or runtime failures
- expected model-configuration failures, including migration and named-role resolution errors,
  are validation failures: print actionable output, return exit code `1`, and do not create crash
  logs or emit stack traces
- exit code `2` for usage and argument errors

## Safety Rules

- read-only default for all inspection commands
- mutating commands require explicit confirmation or `--yes`
- no command may silently broaden exposure policy
- no TUI command may silently change its terminal presentation mode

## Onboarding State Persistence

- onboarding writes progress markers to config metadata
- `netclaw init --resume` continues incomplete onboarding
- `netclaw init --reset` restarts onboarding after explicit confirmation
