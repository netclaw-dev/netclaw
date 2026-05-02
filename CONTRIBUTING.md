# Contributing to Netclaw

This guide covers development workflows, planning tooling, and contributor
conventions. For end-user setup and usage, see [`README.md`](README.md).

## Build and Test

```bash
dotnet build Netclaw.slnx
dotnet test Netclaw.slnx
dotnet slopwatch analyze
```

## Repository Operating Guidance

These files define how planning and implementation work should be routed:

- `AGENTS.md` — agent personas and routing rules
- `PROJECT_CONTEXT.md` — current product direction and constraints
- `TOOLING.md` — tool and infrastructure context
- `CLAUDE.md` — agent constitution and quality bar

## Planning Artifacts

- `docs/prd/` — product requirements and acceptance criteria
- `docs/spec/` — engineering specifications and contracts
- `docs/ui/` — management UI mockups
- `openspec/specs/` — capability specs for ongoing evolution
- `openspec/changes/` — change proposals, design notes, and execution tasks

## OpenSpec Workflow

OpenSpec is initialized for OpenCode in this repository.

Common commands:

- `/opsx:new` — create a new change
- `/opsx:continue` — create next artifact in the change workflow
- `/opsx:ff` — fast-forward: generate all remaining artifacts at once
- `/opsx:apply` — implement tasks from a change
- `/opsx:verify` — verify implementation matches change artifacts
- `/opsx:archive` — archive a completed change

CLI equivalents are available via `openspec --help`.

Netclaw-specific helper skills:

- `.opencode/skills/netclaw-openspec-planning/SKILL.md`
- `.opencode/skills/netclaw-openspec-milestones/SKILL.md`

## RALPH Loop

RALPH infrastructure is available in this repo and tuned for OpenSpec-traceable
execution.

- `ralph-opencode.sh` — OpenCode loop runner
- `ralph.sh` — Claude Code loop runner
- `.claude/skills/ralph-loop.md` — loop discipline with OpenSpec gates
- `.claude/skills/ralph-run-diagnostics.md` — process diagnostics
- `.claude/skills/ralph-output-adversarial-review.md` — adversarial review
- `IMPLEMENTATION_PLAN.md` — RALPH task queue
- `BACKLOG_PARKING_LOT.md` — parked items requiring human decisions

## Smoke Sandbox (Docker)

Developer-only integration sandbox for daemon lifecycle and gateway checks.
This is intentionally script-driven (not a user-facing `netclaw test smoke`
command yet).

```bash
# Start sandbox (build local image + start Ollama + pull tiny model)
scripts/smoke/up.sh

# Start without rebuilding image (useful after pre-building or in CI)
SMOKE_BUILD=0 scripts/smoke/up.sh

# Run smoke checks (daemon start/status/health/stop)
scripts/smoke/check.sh

# Tear down sandbox
scripts/smoke/down.sh

# Optional: remove volumes too
SMOKE_REMOVE_VOLUMES=1 scripts/smoke/down.sh
```

Optional model override:

```bash
SMOKE_OLLAMA_MODEL=qwen2:0.5b scripts/smoke/up.sh
```

Useful timeout overrides for `scripts/smoke/check.sh`:

```bash
# Wait up to 20 minutes for model pull/bootstrap (default: 1200)
INIT_TIMEOUT_SECONDS=1200 scripts/smoke/check.sh

# Per-command timeout inside sandbox (default: 120)
STEP_TIMEOUT_SECONDS=120 scripts/smoke/check.sh
```

### CI Smoke Workflow

`smoke_sandbox` is available in GitHub Actions:

- Runs manually via `workflow_dispatch`.
- Runs on every pull request update.
- Uses Docker Buildx + GitHub Actions cache for smoke image layers.
- Always uploads `smoke-logs-*` artifact (including `check.log`, container
  logs, compose status, daemon log, PID snapshot) for debugging.

## Project Structure

- Solution: `Netclaw.slnx`
- Daemon: `src/Netclaw.Daemon/` (Web API host, `netclawd`)
- CLI: `src/Netclaw.Cli/` (thin client, `netclaw`)
- Actors: `src/Netclaw.Actors/` (session management, persistence, tools)
- Configuration: `src/Netclaw.Configuration/` (paths, providers, models)
- Channels: `src/Netclaw.Channels/` (channel abstractions)
- Slack: `src/Netclaw.Channels.Slack/` (Slack Socket Mode gateway)
- Discord: `src/Netclaw.Channels.Discord/` (Discord gateway)
- Providers: `src/Netclaw.Providers/` (LLM provider implementations)
- OpenAI Compatible: `src/Netclaw.OpenAICompatible/` (OpenAI-compatible API layer)
- Search: `src/Netclaw.Search/` (web search backends)
- Security: `src/Netclaw.Security/` (ACL, device pairing, token management)
- Tools: `src/Netclaw.Tools.Abstractions/` and `src/Netclaw.Tools.Generators/`
