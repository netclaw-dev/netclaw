# Netclaw Demo AppHost

A self-contained .NET Aspire demo that orchestrates NetClaw end-to-end on your
laptop. The goal: `dotnet run` and have a working bot you can chat with — no
Slack workspace, no API keys, no external accounts.

> **Status: work in progress.** This README tracks what's actually wired up.
> Phase 1 (skeleton + sandboxed daemon) and Phase 1.5 (Aspire MCP) are
> functional. Mattermost (Phase 2), Ollama (Phase 3), and the seeded
> conversation experience (Phase 4) are still in flight — see the OpenSpec
> change at `openspec/changes/netclaw-demo-apphost/` for the full plan.

## What it ships today

- `samples/Netclaw.Demo.AppHost` — Aspire AppHost that launches the NetClaw
  daemon as a project resource.
- `samples/Netclaw.Demo.AppHost/.demo-home/` — the daemon's sandboxed home
  directory. Everything the daemon writes (SQLite, encryption keys, identity
  files, secrets, workspaces, logs) lives here, isolated from any host
  NetClaw at `~/.netclaw/`.

## Prerequisites

- .NET 10 SDK (matching `global.json`).
- For later phases: Docker (Mattermost + Ollama containers). Not required for
  the Phase 1 / Phase 1.5 skeleton.

## Launch

From the repo root:

```bash
dotnet run --project samples/Netclaw.Demo.AppHost
```

You'll see logs like:

```
info: Aspire.Hosting.DistributedApplication[0]
      Now listening on: http://localhost:15294
```

Open <http://localhost:15294> in a browser. The Aspire dashboard lists the
`daemon` resource. The daemon binds `127.0.0.1:5299` (not the production
default 5199, so it won't collide with a host-installed NetClaw daemon).
Hitting `http://127.0.0.1:5299/api/health/ready` returns `"healthy"`.

## State isolation

The AppHost sets `NETCLAW_HOME=<repo>/samples/Netclaw.Demo.AppHost/.demo-home/.netclaw`
on the daemon process. `NetclawPaths` (src/Netclaw.Configuration/NetclawPaths.cs:113)
already honors this env var — it's the same knob the smoke harness and the
eval rig use.

To return to a clean slate:

```bash
rm -rf samples/Netclaw.Demo.AppHost/.demo-home/
```

The 8 other `SpecialFolder.UserProfile` callsites in NetClaw
(`PathExpansion`, `ExternalSkillsConfig`, `ShellCommandPolicy`,
`DaemonManager`, `UpdateCommand`, `BrowserAutomationRuntimeDetector`,
`IdentityStepViewModel`, `CrashLogWriter`) intentionally read the real
operator home — they care about your real Chrome install, real
`~/.claude/skills`, real CLI install. `NETCLAW_HOME` doesn't redirect them,
and that's by design. The demo only needs NetClaw's own state isolated.

## Driving the demo from Claude Code (Aspire MCP)

The Aspire CLI ships an MCP server that lets an LLM agent observe and
control a running AppHost. With Claude Code, you can drive the demo
end-to-end without clicking through the dashboard yourself.

One-time setup (run interactively in your terminal, not via Claude Code —
the init flow asks which agent integrations to wire up):

```bash
aspire mcp init
```

That registers the Aspire MCP server in the Claude Code (or other agent)
config and you may need to restart Claude Code for it to pick up the new
MCP server.

Once registered, with the AppHost running, an agent has access to tools like:

- `mcp__aspire__list_apphosts` — discover running AppHosts in the workspace
- `mcp__aspire__select_apphost` — focus on a specific AppHost
- `mcp__aspire__list_resources` — enumerate resources, state, env vars,
  dashboard links
- `mcp__aspire__list_console_logs` — fetch process launch + DCP-level logs
  for a named resource
- `mcp__aspire__execute_resource_command` — run `resource-stop`,
  `resource-restart`, etc.
- `mcp__aspire__list_structured_logs`, `list_traces`, `list_trace_structured_logs`
  — OpenTelemetry-backed observability

A typical agent verification flow looks like:

```text
list_apphosts        # discover the demo
select_apphost       # focus on it
list_resources       # confirm daemon is Running
list_console_logs    # tail the daemon's launch output
```

Direct HTTP probes (e.g., `curl http://127.0.0.1:5299/api/health/ready`)
still work from agent shell tools — useful for endpoints Aspire doesn't
expose via the MCP surface.

## Troubleshooting

- **`aspire mcp init` errors with "Interactive input not supported"** — the
  init flow always wants an interactive terminal even with
  `--non-interactive`. Run it in a real shell, not under an agent harness.
- **Port `5299` in use** — another process holds the demo daemon's port. Run
  `ss -tlnp | grep 5299` to identify it.
- **`.demo-home` gets large** — that's expected; it's the daemon's full
  state tree. Wipe with `rm -rf samples/Netclaw.Demo.AppHost/.demo-home/`
  whenever you want a clean run.
