# Contributing to Netclaw

This guide covers development workflows, planning tooling, and contributor
conventions. For end-user setup and usage, see [`README.md`](README.md) or
visit [netclaw.dev](https://netclaw.dev).

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

## Native Smoke Harness

Developer-only integration harness for daemon lifecycle, gateway checks,
and interactive TUI flows. It runs the real `netclaw` / `netclawd`
binaries natively — no Docker for the binary — against a native Ollama
process. It is script-driven (not a user-facing `netclaw test smoke`
command).

```bash
# Light suite: all PR-gating flow tapes + non-interactive scenarios.
# Publishes the binary, installs vhs, starts Ollama, and pulls the
# smoke models automatically.
scripts/smoke/run-smoke.sh light

# Single tape or scenario by short name (fastest inner loop).
scripts/smoke/run-smoke.sh init-wizard

# Screenshot regression: capture + byte-compare against the baselines.
scripts/smoke/run-smoke.sh screenshots
```

Optional model override:

```bash
SMOKE_OLLAMA_MODEL=qwen2:0.5b scripts/smoke/run-smoke.sh light
```

To iterate against a binary you already built, export its path so the
harness skips the publish step:

```bash
NETCLAW_SMOKE_CLI=./publish/cli/netclaw \
NETCLAW_SMOKE_DAEMON=./publish/daemon/netclawd \
  scripts/smoke/run-smoke.sh light
```

### CI Smoke Workflow

`smoke.yml` runs the harness on every pull request to `dev`, on `dev`
pushes, and nightly. It has three legs, all running in parallel:

- **`Native Smoke (Linux)`** — flow tapes + non-interactive scenarios.
- **`Screenshot Regression (Linux)`** — TUI screenshot byte-comparison.
- **`Native Smoke (macOS)`** — the Apple Silicon leg (flow tapes +
  scenarios; screenshots stay Linux-only).

Each leg uploads a `smoke-*logs-*` artifact (run log, daemon logs,
failure GIFs, screenshot diffs) for debugging.

## Project Structure

- Solution: `Netclaw.slnx`
- Daemon: `src/Netclaw.Daemon/` (Web API host, `netclawd`)
- CLI: `src/Netclaw.Cli/` (thin client, `netclaw`)
- Actors: `src/Netclaw.Actors/` (session management, persistence, tools)
- Configuration: `src/Netclaw.Configuration/` (paths, providers, models)
- Channels: `src/Netclaw.Channels/` (channel abstractions)
- Slack: `src/Netclaw.Channels.Slack/` (Slack Socket Mode gateway)
- Discord: `src/Netclaw.Channels.Discord/` (Discord gateway)
- Mattermost: `src/Netclaw.Channels.Mattermost/` (Mattermost gateway)
- Providers: `src/Netclaw.Providers/` (LLM provider implementations)
- OpenAI Compatible: `src/Netclaw.OpenAICompatible/` (OpenAI-compatible API layer)
- Search: `src/Netclaw.Search/` (web search backends)
- Security: `src/Netclaw.Security/` (ACL, device pairing, token management)
- Tools: `src/Netclaw.Tools.Abstractions/` and `src/Netclaw.Tools.Generators/`

For a step-by-step guide to adding a new chat channel integration (e.g.,
Microsoft Teams, WhatsApp, Signal), see
[Adding a Channel](docs/runbooks/adding-a-channel.md).

## Architecture

Netclaw uses a **daemon + thin client** architecture:

- **`netclawd`** (`src/Netclaw.Daemon/`) — always-on daemon process hosting
  the Akka actor system, LLM sessions, tool execution, and persistence.
  Exposes a SignalR hub at `/hub/session` plus authenticated management
  endpoints for remote clients.

- **`netclaw`** (`src/Netclaw.Cli/`) — thin CLI client for interactive chat,
  daemon management, and configuration. Connects to the daemon via SignalR and
  authenticated HTTP.

## Design Goals

- **Gall's Law:** build the simplest working system first — single-process
  runtime, actor-driven, persistence-backed
- **Default-deny security:** explicit ACL and policy checks everywhere
- **.NET 10 runtime baseline** with Google Protobuf for persistence serialization
- **Session identity is Slack thread:** `{channelId}/{threadTs}`
- **MCP server integration** included from the start

## Building from Source

```bash
# Build everything
dotnet build Netclaw.slnx

# Publish both binaries to a shared output folder
dotnet publish src/Netclaw.Daemon/Netclaw.Daemon.csproj -c Release -o ./out
dotnet publish src/Netclaw.Cli/Netclaw.Cli.csproj -c Release -o ./out
```

Add the output folder to your PATH:

```bash
export PATH="$PWD/out:$PATH"
```

Or point the CLI at the daemon binary explicitly:

```bash
export NETCLAW_DAEMON_PATH="$PWD/out/netclawd"
alias netclaw="$PWD/out/netclaw"
```

## Releasing

Releases are cut by pushing a git tag; `.github/workflows/publish_release_binaries.yml`
does the rest (GitHub release, signed binary manifest + archives to R2, multi-arch Docker
image, and the system skills feed). The pushed tag MUST match `Directory.Build.props` —
`<VersionPrefix>` for a stable tag, and `<VersionPrefix>` + `<VersionSuffix>` for a
prerelease — or the workflow's version gate fails the release.

### Stable release

1. Bump `<VersionPrefix>` in `Directory.Build.props` (e.g. `0.22.1` → `0.22.2`); leave
   `<VersionSuffix>` empty.
2. Add a release-notes section to `RELEASE_NOTES.md` (`#### X.Y.Z YYYY-MM-DD ####`).
3. Commit, then tag and push the bare version:
   ```bash
   git tag 0.22.2 && git push origin 0.22.2
   ```
4. The workflow publishes a normal GitHub release, moves Docker `:latest` and
   `:major.minor`, and sets the manifest `latest` pointer to the new version.

### Beta / prerelease release

Prereleases ship to opt-in testers without touching any stable surface.

1. Set BOTH halves in `Directory.Build.props`:
   ```xml
   <VersionPrefix>0.23.0</VersionPrefix>
   <VersionSuffix>beta.1</VersionSuffix>
   ```
   Use the **dotted** `beta.N` form (`beta.1`, `beta.2`, … `beta.10`) — never `beta1`.
   A non-dotted identifier compares lexically (so `beta10` would rank below `beta2`), and
   the release version gate rejects it.
2. Add a `RELEASE_NOTES.md` section for `0.23.0-beta.1`.
3. Commit, then tag and push the full version (prefix `-` suffix):
   ```bash
   git tag 0.23.0-beta.1 && git push origin 0.23.0-beta.1
   ```
4. The workflow detects the `-` and: marks the GitHub release a **prerelease**; pushes
   only the exact-version Docker tag (leaving `:latest` / `:major.minor` on the newest
   stable); and advances the rolling `:beta` tag and the manifest `latestPrerelease`
   pointer.

Testers opt in:

```bash
curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- --channel beta   # -Channel beta on Windows
docker pull ghcr.io/netclaw-dev/netclaw:beta
NETCLAW_VERSION=0.23.0-beta.1 curl -sSL https://releases.netclaw.dev/install.sh | bash   # pin exact
```

When the matching stable ships, beta installs and `:beta` automatically roll onto it (the
beta channel resolves to the newest of {stable, prerelease}). Stable installs are never
offered a prerelease.
