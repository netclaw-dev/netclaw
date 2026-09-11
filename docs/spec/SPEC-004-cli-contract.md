# SPEC-004: CLI Contract

Source PRDs: `PRD-004`, `PRD-002`, `PRD-001`

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

### 7) External Skill Sync

`netclaw skill sync` runs the daemon's configured external source job immediately.
It does not save configuration or add sources. System skills come from the installed binary.
See the [engineering glossary](GLOSSARY.md) for shared terms.

The command sends an authenticated `POST /api/skills/sync` request.
The endpoint uses the existing daemon authorization policy.
An unauthenticated request receives HTTP 401 and cannot start a pass.
An authenticated request can join a pass that the startup path, timer, or another operator started.

`ServerFeedSkillSyncActor` owns the timer, active pass state, waiters, and lifetime token.
This state is actor-local. The CLI owns only its call-local request wait.
`ServerFeedSkillSyncService` runs one pass and keeps no lifecycle state.
The feed helpers retain the existing durable files and sync receipts.

```text
CLI -> daemon authorization -> ServerFeedSkillSyncActor
  actor stops during a pass: return HTTP 503
  add the caller to the actor-local waiter set
  active pass exists: wait for that pass
  otherwise: call ServerFeedSkillSyncService with the actor lifetime token
  for each enabled feed:
    run the existing RFC skill and native sub-agent sync
    collect its result; continue after a source failure
  refresh the complete inventory through SkillInventoryRefresher
  send the result to the actor
  actor -> send the same result to all waiters
CLI -> print the result -> exit 0 or 1
```

The client has no fixed HTTP timeout for this operation. Existing source timeouts still apply.
The CLI prints a wait notice before it sends the request. The notice explains Ctrl+C.
Ctrl+C cancels the CLI wait. It does not cancel the shared pass.
Actor shutdown cancels the pass and fails its joined requests. Those HTTP requests receive HTTP 503.
An interval of zero disables periodic checks. Startup and manual checks remain available.

| Result | Command behavior |
|---|---|
| All sources and the inventory refresh succeed | Exit 0, including an empty source list |
| A source download fails or its scanner rejects a skill | Exit 1; report counts; let other feeds finish |
| The optional native sidecar does not exist | Report `absent`; RFC sync can succeed |
| An advertised sidecar page is missing or malformed | Report failure; retain existing managed agents |
| The final inventory refresh fails | Exit 1, even if all downloads succeed |
| The daemon predates the endpoint | Exit 1; request a daemon restart |
| The daemon returns HTTP 503 | Exit 1; report that the daemon cannot run the pass now |
| The daemon returns another HTTP error | Exit 1; report the status code, distinct from a connection failure |

The response includes one pass ID, per-source counts, sidecar status, and the final inventory result.
The service assigns the pass ID before source work and includes it in its start and completion logs.
The response contains no derived overall success field. The CLI computes its exit code from the source and inventory results.
Overlapping callers receive the same pass ID. Source errors in this response do not include credentials or remote response bodies.
Inventory rejection counts can include pre-existing source conflicts. They do not make a completed inventory refresh fail.
This command does not add a transaction across feed files, the registry, and the prompt index.
It preserves the existing per-skill replacement and prune rules.
For a prune, the changed count includes each obsolete skill once if its receipt or owned directory is removed.
A missing directory does not prevent receipt removal. An orphan directory can count without a receipt.
A directory deletion failure increments the failure count. The result can report both a removed receipt and a failed directory deletion.

For example, a healthy feed can update while another feed returns HTTP 500. The command reports both results and exits 1.
A rejected skill retains its prior bytes and receipt. Other accepted skills from that feed can still update.
Download failures do not create security alerts. This change does not alter the existing scanner or alert policy.

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
- mutating commands require explicit confirmation or `--yes`, except `skill sync`; that command explicitly requests the existing daemon job
- no command may silently broaden exposure policy

## Onboarding State Persistence

- onboarding writes progress markers to config metadata
- `netclaw init --resume` continues incomplete onboarding
- `netclaw init --reset` restarts onboarding after explicit confirmation
