## Context

Netclaw's current implementation plan (Phase 1) reaches the Slack adapter at
Task 1.13 — the first opportunity for end-to-end validation. This creates a
gap: Tasks 1.1-1.12 build actor framework, persistence, tools, scheduling,
and memory, but none of them can be validated end-to-end without live Slack
credentials and infrastructure.

Adding a local TUI adapter (`netclaw chat`) as the first input adapter closes
this gap. The full agent stack — session actors, tool calls, streaming
responses, MCP integration — can be validated locally through the terminal.
Slack becomes just another adapter plugged into an already-validated system.

Config hot-reload addresses a second operational need: a long-running homelab
agent should not require process restarts for routine configuration changes
(ACL rules, provider settings, MCP profiles, schedules).

## Goals / Non-Goals

**Goals:**

- Provide `netclaw chat` as the first E2E validation path for the agent stack
- Deliver `netclaw init` as an interactive TUI onboarding wizard
- Add `netclaw run` as the explicit daemon entry point
- Route all CLI commands through Cocona for consistent DI and command parsing
- Enable hot-reload for operational config files without process restart
- Resequence implementation plan to TUI-first, Slack-second

**Non-Goals:**

- Web UI adapter (Phase 5)
- Webhook adapter (Phase 2)
- Hot-reload for personality files, project registry, or environment inventory
  (these are session-scoped; change requires session reboot)
- TUI-based ambient monitoring
- Custom Termina component development (use existing component library)

## Decisions

### Decision 1: Cocona for CLI command routing

**Choice:** Cocona 2.3.0

**Alternatives considered:**
- `System.CommandLine` — more complex, verbose attribute model, no built-in DI
- `Spectre.Console.Cli` — good CLI, but we need TUI (not just formatted output)
- Raw `WebApplication` with custom arg parsing — current state, not scalable

**Rationale:** Cocona is lightweight, convention-based, and has built-in DI
integration through `Microsoft.Extensions.DependencyInjection`. Commands are
simple classes with method parameters mapped to CLI arguments. It integrates
cleanly with `IHostedService` for daemon mode.

### Decision 2: Termina 0.5.1 for TUI

**Choice:** Termina 0.5.1 (owned library)

**Rationale:** Termina is Aaron's own reactive MVVM TUI framework for .NET 10.
It provides the component vocabulary needed (PanelNode, TextInputNode,
SelectionListNode, StreamingTextNode, SpinnerNode) and is AOT-compatible.
Only `netclaw init` and `netclaw chat` use TUI — all other commands stay
plain CLI via Cocona.

### Decision 3: TUI adapter hosts actor system in-process

**Choice:** `netclaw chat` hosts the full Akka actor system in-process, same
as `netclaw run` does for daemon mode.

**Alternatives considered:**
- Connect to running daemon via IPC — adds complexity, requires daemon running
- Lightweight mode without actors — loses the validation benefit

**Rationale:** The whole point is validating the same actor stack that runs in
production. In-process hosting means `netclaw chat` exercises the exact same
code paths as Slack-driven sessions, just with a different input adapter.

### Decision 4: FileSystemWatcher for config hot-reload

**Choice:** `FileSystemWatcher` + 500ms debounce + validate-before-apply

**Alternatives considered:**
- Polling with interval — wastes CPU, slower to detect changes
- `inotify` directly — platform-specific, FSW already wraps this on Linux
- Config server / etcd — over-engineered for single-process homelab agent

**Rationale:** `FileSystemWatcher` is built into .NET, works on all platforms,
and is sufficient for a single-process agent watching a handful of config files.
The debounce window (500ms) prevents rapid-fire reloads during file save
operations. Validate-before-apply ensures invalid configs never reach the
runtime.

### Decision 5: Watched vs unwatched file classification

**Watched (hot-reloaded):**
- ACL rules — policy engine refresh
- Provider config — `IChatClient` rebuild
- MCP profiles — server reconnect/disconnect
- Schedule definitions — timer reconfiguration

**Unwatched (require restart or session reboot):**
- Personality files (PERSONALITY.md, INSTRUCTIONS.md, USER.md) — cached in LLM
  context at session start
- Project registry — loaded at session start
- Environment inventory — loaded at startup

**Rationale:** Watched files control runtime behavior that can be atomically
swapped. Unwatched files are loaded into LLM context at session start and
changing them mid-session would create inconsistency.

### Decision 6: Actor notification on config change

Config changes are dispatched to owning actors via Akka pub/sub:

| Config File | Owning Actor/Service | Notification |
|-------------|---------------------|--------------|
| ACL rules | Policy engine | Re-evaluate grants for active sessions |
| Provider config | Provider factory | Rebuild `IChatClient` instances |
| MCP profiles | MCP manager | Reconnect/disconnect affected servers |
| Schedule definitions | ScheduleManagerActor | Reconfigure timers |

This preserves actor boundaries — the `ConfigWatcherService` publishes change
events, actors subscribe to the topics they care about.

## Risks / Trade-offs

**[Risk]** FileSystemWatcher can miss events on some filesystems (NFS, Docker
volumes).
→ **Mitigation:** Target is local disk on pi1. Document that networked
filesystems are unsupported for config directory. Add `netclaw config validate`
as manual fallback.

**[Risk]** Debounce window too short could cause partial reads of multi-file
config changes.
→ **Mitigation:** 500ms default is conservative. Each file is validated
independently. Operator can also use `netclaw config validate` before manual
reload.

**[Risk]** TUI adapter adds maintenance surface for a dev-only input path.
→ **Mitigation:** TUI adapter implements the same adapter contract as Slack.
It's not a separate code path — it's the same `SendUserMessage` → session →
broadcast pipeline. The TUI-specific code is only the rendering layer.

**[Risk]** Cocona + Termina add two new package dependencies.
→ **Mitigation:** Both are lightweight. Cocona is ~50KB. Termina is owned and
maintained by the project owner. Both support AOT compilation.

**[Failure mode]** Config file deleted while being watched.
→ **Recovery:** `ConfigWatcherService` treats deletion as "no config" and logs
a warning. Existing runtime config remains in effect until a valid replacement
is written. Does not crash the process.

**[Failure mode]** Invalid config written to watched file.
→ **Recovery:** Validate-before-apply rejects the change. Previous valid config
remains active. Diagnostic log entry includes validation errors and the file
path. `netclaw doctor` reports the validation failure.
