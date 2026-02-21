# Netclaw

Netclaw is a Slack-connected homelab assistant built on top of a minimal
actor-driven session framework called Akka.Agents.

This repository currently contains a documentation-first implementation track:

- product requirements documents (PRDs)
- technical specifications
- OpenSpec change artifacts and capability specs
- management UI mockups and CLI contracts

Code implementation follows these artifacts.

## Current Focus

MVP target: run Netclaw on `pi1`, reply in Slack threads, persist sessions
across restarts, and compact long conversations without losing context.

Primary constraints:

- Gall's Law: build the simplest working system first
- single-process runtime for MVP
- .NET 10 runtime baseline
- default-deny ACL and explicit policy checks
- session identity is Slack thread: `{channelId}/{threadTs}`
- MCP server integration is included in MVP scope
- protobuf-net for persistence types (no direct serialization of
  `Microsoft.Extensions.AI` message types)

## Scaffold

- Solution: `Netclaw.slnx`
- Framework project: `src/Akka.Agents/Akka.Agents.csproj`
- Host application: `src/Netclaw.App/Netclaw.App.csproj` (minimal Web API host)

Build:

- `dotnet build Netclaw.slnx`

## Planning Artifacts

- `docs/prd/` - product requirements and acceptance criteria
- `docs/spec/` - engineering specifications and contracts
- `docs/ui/` - management UI mockups
- `openspec/specs/` - capability specs for ongoing evolution
- `openspec/changes/` - change proposals, design notes, and execution tasks

## OpenSpec Workflow

OpenSpec is initialized for OpenCode in this repository.

Common commands:

- `/opsx:new`
- `/opsx:continue`
- `/opsx:ff`
- `/opsx:apply`
- `/opsx:verify`
- `/opsx:archive`

CLI equivalents are available via `openspec --help`.

Netclaw-specific helper skills are available at:

- `.opencode/skills/netclaw-openspec-planning/SKILL.md`
- `.opencode/skills/netclaw-openspec-milestones/SKILL.md`

## RALPH Loop

RALPH infrastructure is available in this repo and tuned for OpenSpec-traceable
execution.

- `ralph-opencode.sh` - OpenCode loop runner
- `ralph.sh` - Claude Code loop runner
- `.claude/skills/ralph-loop.md` - loop discipline with OpenSpec gates
- `.claude/skills/ralph-run-diagnostics.md` - process diagnostics
- `.claude/skills/ralph-output-adversarial-review.md` - adversarial review
- `IMPLEMENTATION_PLAN.md` - RALPH task queue
- `BACKLOG_PARKING_LOT.md` - parked items requiring human decisions

## Bootstrap Docs

Repository operating guidance lives in:

- `AGENTS.md`
- `PROJECT_CONTEXT.md`
- `TOOLING.md`

These files define how planning and implementation work should be routed.
