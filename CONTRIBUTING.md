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
