## Why

The `netclaw init` wizard silently infers security posture and channel
audiences from the Exposure Mode selection. There are no TUI screens where
the user explicitly chooses deployment posture, assigns per-channel audiences,
or searches for Slack users by display name. This was specced in the archived
`2026-03-23-trust-context-security-policy` change (task 3.3) but only the
backend inference was implemented — the interactive TUI screens were never
built. The application is effectively unusable for new users who need to
understand and configure their security model.

Ref: PRD-001 FR-006 (Layered System Prompt), PRD-002 (Gateway Security),
PRD-004 (CLI Onboarding and Config).

## What Changes

- **Reorder wizard steps**: Security posture moves to step 3 (after
  ChatServices, before ACL) so downstream steps inherit correct defaults
- **New SecurityPosture step**: Interactive selection of deployment posture
  (Personal/Team/Public) with explanatory text for each option
- **Rework Channels step**: Break channel management out of ChatServices into
  its own step with dynamic add/remove and per-channel audience cycling via
  ←/→ keys. Channels populated from `conversations.list` API
- **Rework ACL/Owner step**: Type-to-filter user search against Slack
  `users.list` API instead of requiring raw user IDs
- **Remove Exposure step**: Fold network exposure concept into SecurityPosture
  (posture implies exposure level)
- Shell mode derived from posture: Personal → HostAllowed, Team/Public → Off

## Capabilities

### New Capabilities

- `security-posture-tui`: Interactive TUI step for deployment posture
  selection with audience defaults derivation
- `channel-audience-tui`: Per-channel audience assignment with ←/→ cycling,
  dynamic channel add/remove via Slack API

### Modified Capabilities

- `netclaw-onboarding`: Wizard step order changes, Exposure step removed,
  Channels broken out of ChatServices, ACL uses Slack user lookup
- `netclaw-cli`: `ISlackProbe` extended with `ListUsersAsync` for
  type-to-filter user search during init

## Impact

- `src/Netclaw.Cli/Tui/InitWizardPage.cs` — New step renderers, reordered
  step flow, ←/→ key handling for audience cycling, user search UI
- `src/Netclaw.Cli/Tui/InitWizardViewModel.cs` — New step state, user search
  state, channel audience editing state, reordered `WizardStep` enum
- `src/Netclaw.Channels.Slack/SlackProbe.cs` — Add `ListUsersAsync` for
  user search/resolution
- `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` — no
  schema changes (Security and ChannelAudiences already defined)
- **BREAKING**: `WizardStep` enum order changes. No external consumers.
