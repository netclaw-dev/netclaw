## Why

The deployed Group Chat search stops after small batches and requires repeated operator actions before it can reach a match.
The owner reports an unchanged ten-user count after several attempts, so the TUI does not show useful progress.

## What Changes

- Continue chat-title discovery automatically within a bounded search run.
- Show cumulative request, chat-record, and completed-user counts after each batch.
- Retain discovered matches and let the operator stop, resume, or select a result.
- Visit other users before a source with many chat pages consumes the search run.
- Request only chat metadata needed for title matches; omit participant expansion during the scan.
- Reject repeated metadata pages and preserve the last successful checkpoint after cancellation or failure.

This change supersedes the mandatory manual continuation decision in `correct-group-chat-name-search`.
That change still defines title matching and the canonical-ID save boundary.

## Capabilities

### New Capabilities

- `teams-group-chat-name-search`: Extend the existing unarchived capability with automatic progress, fair source traversal, and stop/resume behavior.

### Modified Capabilities

None. The Teams name-search capability has not entered `openspec/specs`.

## Impact

Source PRDs: [PRD-004](../../../docs/prd/PRD-004-cli-onboarding-and-config.md) and [PRD-009](../../../docs/prd/PRD-009-input-adapters-and-unified-input.md).

The change affects the Teams directory contract, Graph adapter, Group Chat TUI, tests, tape, and operator guidance.
The scope excludes other channel adapters, runtime ACL changes, authentication changes, message access, and tenant consent changes.
No configuration migration or actor protocol change is required.

### Security and operational impact

The adapter retains `User.Read.All` and optional `Chat.ReadBasic.All` application permissions.
One operator action can make more metadata requests, so each run has finite batch and time limits.
The UI retains explicit stop and resume controls.
Temporary search metadata grants no access and never enters the configured principal lists.
