## Why

Source PRDs: `PRD-001` (primary), `PRD-002`, `PRD-004`

Netclaw can already deliver outbound operational notifications to webhook
targets, but operators still have to hand-edit JSON to inspect, add, remove, or
probe those targets. That makes a security-sensitive config path harder to use
correctly and slows down incident-time setup, so MVP needs an explicit CLI for
notification webhook management.

## What Changes

- Add plain CLI commands to list, add, remove, and test outbound notification
  webhook targets without opening a text editor.
- Keep notification secrets out of `netclaw.json` by writing webhook URLs and
  auth-like headers to `secrets.json` or accepting them through `NETCLAW_`
  environment variables.
- Reuse the existing notification config validation rules so CLI writes fail
  closed before persisting invalid webhook settings.
- Add operator-facing output that shows target identity, config location, and
  safe remediation when a notification webhook command fails.
- Update docs for the notification webhook command surface and config layout.

## Capabilities

### New Capabilities

- `notification-webhook-cli`: Offline CLI workflow for managing outbound
  operational notification webhook targets, including add/remove/list/test and
  secret-safe config writes.

### Modified Capabilities

- `netclaw-cli`: The CLI command surface gains notification webhook management
  commands with automation-friendly exit codes and remediation-first output.

## In Scope (MVP)

- Offline commands for listing configured notification webhook targets.
- Guided add/remove flows that update `netclaw.json` and `secrets.json` without
  exposing webhook URLs or header values.
- A connectivity test command that sends a bounded probe to a selected target and
  reports HTTP success or failure.
- Shared validation and secure defaults consistent with existing notification
  config rules.

## Out of Scope

- New notification delivery backends beyond HTTP webhooks.
- Hot reload of notification config into the running daemon.
- Rich notification templating, routing rules, or per-alert message previews.
- Interactive TUI management for notification webhooks.

## Impact

- **Code/Runtime**: `Netclaw.Cli` command routing, config read/write helpers,
  notification validation, and HTTP probe path for webhook tests.
- **Security**: preserves default-deny and fail-closed behavior by separating
  secret-bearing webhook URLs and headers from base config and rejecting invalid
  targets before writing files.
- **Operations**: gives operators a faster, safer way to manage notification
  endpoints during setup and troubleshooting.
- **Docs**: updates CLI and configuration docs to describe the new command
  surface and file layout.
