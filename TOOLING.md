# Tooling Inventory - Netclaw

## Runtime and Build

- `.NET SDK`: pinned by `global.json` (currently .NET 10 line)
- `dotnet` CLI: build, test, run, restore, local tool execution
- local tools configured in `.config/dotnet-tools.json`
- solution scaffold: `Netclaw.slnx` with `src/Akka.Agents` and
  `src/Netclaw.App`

## Planning and Spec Tooling

- `OpenSpec` CLI: installed and initialized in this repo
  - OpenCode command/skill files generated under `.opencode/`
  - repository artifacts under `openspec/`
- markdown docs under `docs/prd/`, `docs/spec/`, and `docs/ui/`
- RALPH loop infrastructure for iterative implementation
  - `ralph-opencode.sh`, `ralph.sh`
  - local Claude skills under `.claude/skills/`
  - flight recorder at `.ralph/runs/<run-id>/`

## Copyright Header Enforcement

| Command | Purpose |
|---------|---------|
| `scripts/Add-FileHeaders.ps1` | Add Petabridge copyright headers to all `.cs` files |
| `scripts/Add-FileHeaders.ps1 -Verify` | CI: check all files have headers (exit 1 if missing) |
| `scripts/Add-FileHeaders.ps1 -WhatIf` | Preview which files need headers |

## Interactive CLI Smoke Tests (Tape Harness)

A VHS-driven smoke harness exercises the interactive Termina TUI surface
that `scripts/smoke/check.sh` cannot reach (Spectre-style prompts,
wizard flows, model/provider/webhook TUIs). Tape bodies live at
`tests/smoke-interactive/tapes/<name>.tape`; sibling assertion scripts at
`tests/smoke-interactive/assertions/<name>.sh` validate the artefacts
each tape produced. The same scripts run in CI (inside the Smoke
Sandbox job) and locally — agents working on TUI code SHOULD run the
harness before declaring a change done.

| Command | Purpose |
|---------|---------|
| `./scripts/smoke/run-tapes.sh light` | PR-gating subset; auto brings the smoke compose stack up and tears down |
| `./scripts/smoke/run-tapes.sh full` | Full nightly suite (placeholder: identical to light until backfilled) |
| `./scripts/smoke/run-tapes.sh <tape-name>` | Single tape, e.g. `init-wizard` |
| `./scripts/smoke/run-tapes.sh <tape-name> --keep-stack` | Leave the compose stack running between iterations |
| `./scripts/smoke/run-tapes.sh <tape-name> --no-up --keep-stack` | Re-run against an already-running stack (fastest inner loop) |
| `./scripts/smoke/install-vhs.sh` | Idempotent VHS install (Linux/x86_64 only; macOS/Windows install manually) |

When a tape fails, `smoke-logs/tapes/<name>/` collects: a debug GIF of the
last frame, the combined tape file, container logs, and a tarball of
the produced `NETCLAW_HOME`. CI uploads the same directory as a job
artefact.

**Authoring conventions are in `tests/smoke-interactive/tapes/README.md`**
— the short version: `Wait+Screen /pattern/` only (no `Sleep`), 1400×800
default surface, no `Screenshot` directives, pair every non-trivial tape
with an assertion script that re-validates `netclaw doctor` and the
relevant `--json` output.

## Install Script Smoke Test

`scripts/smoke/install-smoke.sh` and `scripts/smoke/install-smoke.ps1` are
hermetic regression tests for the installers (`scripts/install.sh` and
`scripts/install.ps1`). They need no network, no `dotnet` build, and no
running daemon — each serves a generated manifest and stand-in archives
from `localhost`.

| Command | Purpose |
|---------|---------|
| `bash scripts/smoke/install-smoke.sh` | Smoke-test the `curl \| bash` installer (Linux/macOS) |
| `pwsh scripts/smoke/install-smoke.ps1` | Smoke-test the PowerShell installer (Windows) |

`install-smoke.sh` covers two layers:

- **Detection matrix** — runs `install.sh --dry-run` under `uname`/`sysctl`
  shims to assert every supported OS/arch resolves to the right RID
  (`linux-x64`, `linux-arm64`, `osx-arm64`) and that Intel Macs and
  unsupported OSes are rejected cleanly. This runs identically on any host.
- **Mechanical check** — one real install of a stand-in archive on the
  host's native RID, exercising download → checksum → `tar` extract → `cp`.

`install-smoke.ps1` is the Windows counterpart: a `-DryRun` resolution
check plus a real stand-in install exercising download → checksum →
`Expand-Archive` → copy.

The `install-smoke` job in `pr_validation.yml` runs these on
`ubuntu-latest`, `macos-latest`, and `windows-latest` on every PR. Both
installers also support `--dry-run` / `-DryRun` on their own — they report
which binary *would* be installed for the current platform without
touching the system.

## Source Control and CI Signals

- `git` repository with active `dev` branch
- GitHub Actions workflows in `.github/workflows/`
- Azure pipeline templates in `.azure/`

## External Integrations (Planned for MVP)

- Slack Socket Mode
  - requires bot token and app token
  - no public inbound HTTP required for base interaction
- SQLite for Akka.Persistence journal and snapshots (in-memory for tests)
- MCP servers for external tool integration (MVP requirement)
- local Ollama endpoint can be used for optional smoke tests
  - local dev host: `my-gpu-server` on Tailscale (`http://my-gpu-server:11434`)
  - preferred model: `qwen3:30b` (fallback `qwen3:14b`)

## Security-Relevant Surfaces

- Slack inbound message events (untrusted input)
- tool execution surfaces (web, file read/write, shell)
- ACL configuration and policy evaluation
- system prompt and policy files loaded from disk

## Operator Interfaces (Planned)

- CLI for onboarding/config validation, policy diagnostics, and session
  operations
- management UI (ops console) for health, session inspection, ACL editing, and
  diagnostics

## Working Assumptions

- single-process architecture during MVP
- operator-controlled host and credentials
- default-deny policy with explicit per-channel and per-sender allow rules
- required CI tests do not depend on live model providers
- `my-gpu-server` Ollama access is local-dev only and not available in CI/CD
