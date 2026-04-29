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
  - local dev host: `big-gpu` on Tailscale (`http://big-gpu:11434`)
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
- `big-gpu` Ollama access is local-dev only and not available in CI/CD
