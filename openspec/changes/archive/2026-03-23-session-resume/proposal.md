## Why

Sessions are persistent and recoverable (PRD-001 FR-001, netclaw-session spec),
but users have no way to browse or resume them. A passivated session's full
conversation survives in the Akka journal, yet the only entry point is "start a
new chat." This is the gap that OpenCode `/sessions` and Claude Code `/resume`
fill in competing tools — users expect to pick up where they left off.

PRD-004 CLI-005 already calls for `session list|inspect|compact` commands but
none have been implemented. The SQLite catalog (`SessionCatalogService`) and the
`GET /api/sessions` endpoint already exist, providing the data layer. The actor
infrastructure already supports multiple concurrent subscribers via `JoinSession`
with `OutputFilter` bitmask — a TUI client can subscribe to a live Slack session
or rehydrate a passivated one with zero actor-layer changes.

## What Changes

- **TUI session browser**: new `sessions` Terminal.Gui page listing recent
  sessions from the catalog (title, channel, turn count, last activity). Select
  a session to resume it in the existing chat page.
- **CLI `--resume <id>` flag**: `netclaw chat --resume <session-id>` skips the
  browser and opens the chat page directly attached to the specified session.
- **SessionRegistry join path**: new `JoinExistingSessionAsync` method that
  materializes a `SessionPipeline` against an arbitrary session ID (instead of
  always creating a new one). This is the plumbing that lets both the TUI browser
  and `--resume` flag attach to sessions originally created by any channel.
- **DaemonClient API surface**: new `ResumeSessionAsync(sessionId)` and
  `ListSessionsAsync()` SignalR methods bridging CLI to daemon.
- **Session history replay on join**: when joining a recovered session, the
  `SessionJoined` response already includes turn count — the TUI renders a
  "Resumed session (N turns)" indicator rather than replaying full history.

## Capabilities

### New Capabilities

- `session-resume`: Session browsing, selection, and resumption from any channel.
  Covers the TUI list view, CLI `--resume` flag, `SessionRegistry` join path,
  and SignalR API surface for session listing and attachment.

### Modified Capabilities

- `netclaw-cli`: Adds `--resume <id>` flag to `chat` command and `sessions`
  subcommand for the TUI browser (PRD-004 CLI-005).
- `netclaw-session`: Multi-channel subscription behavior — a TUI client joining
  a Slack-originated session receives output alongside the Slack subscriber.

## Impact

- **SessionRegistry** (`src/Netclaw.Daemon/Gateway/`): new join path, new
  SignalR hub methods for listing and resuming
- **DaemonClient** (`src/Netclaw.Cli/Daemon/`): new API methods
- **CLI Program.cs**: new `--resume` option on `chat` command
- **TUI**: new sessions list page/view, navigation wiring to chat page
- **No actor-layer changes**: `LlmSessionActor` already supports multiple
  `JoinSession` subscribers and persistence recovery
- **No persistence schema changes**: session catalog table already has all
  needed columns
- **Security**: resumed sessions inherit the original session's ACL context;
  no new privilege escalation vectors since the daemon is single-user
