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

The native smoke harness exercises the interactive Termina TUI surface
that the non-interactive scenarios cannot reach (Spectre-style prompts,
wizard flows, model/provider/webhook TUIs). It drives the **real native
binary** — no Docker. Tape bodies live at `tests/smoke/tapes/<name>.tape`;
sibling assertion scripts at `tests/smoke/assertions/<name>.sh` validate
the artefacts each tape produced. The same `run-smoke.sh` entrypoint runs
in CI (`smoke.yml`) and locally — agents working on
TUI code SHOULD run the harness before declaring a change done.

| Command | Purpose |
|---------|---------|
| `./scripts/smoke/run-smoke.sh light` | PR-gating subset: all flow tapes + non-interactive scenarios |
| `./scripts/smoke/run-smoke.sh full` | Full suite (placeholder: identical to light until backfilled) |
| `./scripts/smoke/run-smoke.sh <name>` | Single tape or scenario, e.g. `init-wizard` (fastest inner loop) |
| `./scripts/smoke/run-smoke.sh screenshots` | Screenshot regression: capture + byte-compare against baselines |
| `./scripts/smoke/install-vhs.sh` | Idempotent VHS install (Linux/x86_64 + macOS via Homebrew) |

`run-smoke.sh` publishes the binary (or uses `NETCLAW_SMOKE_CLI` /
`NETCLAW_SMOKE_DAEMON` if exported), installs `vhs`, starts a native
`ollama serve`, and pulls the smoke models automatically.

When a tape fails, `smoke-logs/tapes/<name>/` collects: a debug GIF of the
last frame, the combined tape file, daemon logs, and the produced
`NETCLAW_HOME`. CI uploads the `smoke-logs` directory as a job artefact.

**Authoring conventions are in `tests/smoke/tapes/README.md`**
— the short version: `Wait+Screen /pattern/` only (no `Sleep`), 1400×800
default surface, no `Screenshot` directives in flow tapes, pair every
non-trivial tape with an assertion script that re-validates `netclaw
doctor` and the
relevant `--json` output.

## Demo AppHost Smoke Test (Slow)

`samples/Netclaw.Demo.AppHost.IntegrationTests` is an Aspire-driven
end-to-end test that boots the demo AppHost (`samples/Netclaw.Demo.AppHost`),
waits for every resource — Mattermost container, Ollama container,
`qwen3.5:2b-q4_K_M` model, NetClaw daemon project — to reach healthy, posts a
Mattermost message via REST as the seeded test user, and asserts the
wiring routes the message through.

Opt-in by design: the test self-skips unless
`NETCLAW_RUN_DEMO_SMOKE=1` is set (same pattern as the Mattermost
integration tests). The `[Trait("Category", "SlowSmoke")]` trait is a
secondary filter for local-dev runs. A bare `dotnet test` on any CI
runner therefore reports the test as skipped, not failed. Invoke with:

```bash
NETCLAW_RUN_DEMO_SMOKE=1 \
  dotnet test samples/Netclaw.Demo.AppHost.IntegrationTests \
    --filter Category=SlowSmoke
```

Prerequisites: Docker daemon reachable, ~4GB of disk free on a cold
cache (Mattermost preview + Ollama image + `qwen3.5:2b-q4_K_M` weights). Warm
runs reuse cached images and the model volume.

The test's bot-reply wait is best-effort and configurable. On a
CPU-only host inference takes minutes; on GPU it's <30s. Override the
default 5-minute reply window:

```bash
NETCLAW_RUN_DEMO_SMOKE=1 \
NETCLAW_DEMO_TEST_REPLY_TIMEOUT_SECONDS=900 \
  dotnet test samples/Netclaw.Demo.AppHost.IntegrationTests --filter Category=SlowSmoke
```

If the timeout elapses without a reply, the test still passes — the
structural assertions (every resource healthy, message posted into
Mattermost) prove the wiring. The latency is printed to stdout so a CI
run can flag a slow-inference regression.

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

## Logging Conventions

- Carry cross-cutting identity (channel/adapter, session, entity ids) as
  **structured context via `ILoggingAdapter.WithContext(...)`**, set once where
  the logger is created — not baked into each message template. Actors already
  do this (e.g. `Context.GetLogger().WithContext("Adapter", "slack")`).
- Do **not** prefix the channel/adapter into the message text
  (`slack_attachment_rejected …`). Emit a plain semantic event
  (`attachment_rejected …`); the `Adapter` context disambiguates the source.
  Prefixing double-stamps what the context already carries and forces shared
  code to take a channel-name parameter purely for logging.
- Shared/abstracted helpers that take an `ILoggingAdapter` should rely on the
  caller's enriched context rather than re-passing identity strings.
- For `Microsoft.Extensions.Logging` (`ILogger<T>`) call sites, the logger
  **category** already identifies the type; prefer that (or `BeginScope`) over
  repeating the class/channel name in the message.

See `getakka.net` → Utilities → Logging → "Context enrichment and scopes".

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
