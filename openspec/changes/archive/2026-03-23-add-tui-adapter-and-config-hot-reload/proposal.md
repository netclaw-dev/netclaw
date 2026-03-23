## Why

The current implementation plan requires Slack Socket Mode as the first
end-to-end validation path (Task 1.13), but Slack credentials and live
infrastructure make autonomous RALPH execution fragile. Adding a local TUI
adapter (`netclaw chat`) as the first input adapter lets us validate the entire
agent stack — session actors, tool calls, streaming responses, MCP integration —
without any external dependencies. Config hot-reload removes the need to restart
the process when operational configuration changes, which is critical for a
long-running homelab agent.

Source PRDs: `PRD-001` (FR-016), `PRD-004` (CLI-010, CLI-011, CLI-012),
`PRD-009` (INPUT-005).

## What Changes

- Add Cocona as the CLI command routing framework (replaces raw
  `WebApplication` entry point)
- Add Termina 0.5.1 as the TUI framework for interactive commands
- Add `netclaw chat` command — interactive agent prompt with streaming
  responses, tool activity display, and MCP status via TUI adapter
- Add `netclaw init` command — 7-step onboarding wizard delivered through TUI
- Add `netclaw run` command — explicit daemon entry point (Slack + timers +
  health endpoints, no TUI)
- Add `netclaw doctor` and all other plain CLI commands via Cocona
- Add TUI input adapter implementing the adapter contract (`SendUserMessage`
  with entity key `tui/{sessionId}`)
- Add config hot-reload for operational configuration files (ACL, providers,
  MCP profiles, schedules) via `FileSystemWatcher` + debounce +
  validate-before-apply
- Resequence implementation plan: TUI-first validation before Slack adapter

### In Scope (MVP)

- TUI adapter as Phase 1 input source
- Cocona command routing for full PRD-004 command surface
- Termina TUI for `netclaw init` and `netclaw chat`
- Config hot-reload for ACL, provider, MCP, and schedule files
- Actor notification on config change (policy refresh, provider rebuild,
  MCP reconnect)

### Out of Scope

- Web UI adapter (Phase 5)
- Webhook adapter (Phase 2)
- Hot-reload of personality files, project registry, or environment inventory
- TUI-based ambient monitoring

## Capabilities

### New Capabilities

- `netclaw-config-hot-reload`: FileSystemWatcher-based hot-reload for
  operational config files. Debounce, validate-before-apply, actor
  notification, watched vs unwatched file classification.

### Modified Capabilities

- `netclaw-cli`: Add Cocona framework requirement, TUI command classification
  (TUI-interactive vs plain-CLI), `netclaw run` daemon entry point,
  `netclaw chat` local adapter command
- `netclaw-input-adapters`: Add TUI adapter as Phase 1 input source with
  entity key pattern `tui/{sessionId}` and adapter contract implementation
- `netclaw-onboarding`: Add TUI wizard as onboarding delivery mechanism
  (Termina-based 7-step wizard for `netclaw init`)
- `netclaw-session`: Add config hot-reload integration — re-evaluate tool
  grants when ACL changes, provider swap on config change, MCP reconnect
  on profile change

## Impact

- **Runtime entry point**: `Program.cs` rewritten from `WebApplication` to
  Cocona command host with Termina integration
- **Package dependencies**: Cocona 2.3.0, Termina 0.5.1 added to
  `Directory.Packages.props` and `Netclaw.App.csproj`
- **New source files**: `Commands/` directory for Cocona commands, `Tui/`
  directory for Termina pages and view models, `Adapters/TuiInputAdapter.cs`,
  `Services/ConfigWatcherService.cs`
- **Implementation plan**: Tasks 1.13-1.22 resequenced to TUI-first ordering
- **Security**: No new attack surface — TUI runs locally, config hot-reload
  validates before applying, invalid configs are rejected
- **Operational**: Process no longer requires restart for ACL/provider/MCP/
  schedule config changes
