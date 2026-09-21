## Why

The Group Chat add screen searches users, so an operator cannot find a chat by its visible Teams title.
The owner confirmed this failure during deployment tests after PR #65.

## What Changes

- Open a Group Chat name input directly from the Group Chat add action.
- Match partial and full chat titles without case sensitivity.
- Read tenant user IDs and their chat metadata in bounded batches through the existing Graph application credentials.
- Return opaque continuations and report incomplete coverage or Graph failures explicitly.
- Preserve canonical chat IDs, existing access rules, optional consent, and advanced manual entry.
- Add Graph, TUI, and native input regression coverage.

This correction supersedes the selected-user discovery and no-tenant-enumeration decisions in `complete-teams-tui-access-management`.
The owner rejected the participant-first flow because it did not meet the requested chat-name search behavior.
That prior change remains historical context; this change defines the corrected discovery contract.

## Capabilities

### New Capabilities

- `teams-group-chat-name-search`: Direct chat-title search with bounded application-only metadata discovery and explicit completion state.

### Modified Capabilities

None. The earlier Teams discovery deltas have not entered `openspec/specs`.

## Impact

Source PRDs: [PRD-004](../../../docs/prd/PRD-004-cli-onboarding-and-config.md) and [PRD-009](../../../docs/prd/PRD-009-input-adapters-and-unified-input.md).

The change affects the Teams directory contract, Graph adapter, configuration TUI, tests, native tape, and operator guidance.

### Scope

The scope includes chat-title discovery, its error states, and the existing canonical-ID add flow.
It excludes message history, new authentication flows, installation changes, runtime authorization changes, merges, and deployments.
Slack Socket Mode and other adapters retain their current behavior.

### Security and operational impact

The application reuses `User.Read.All` and optional `Chat.ReadBasic.All`; it adds no Graph permission.
Search now reads tenant user IDs internally instead of asking the operator to choose a participant.
Each bounded batch may require another explicit `Continue search` action.
Discovery data stays temporary and grants no access.
Only a selected, validated canonical chat ID can enter the existing configuration writer.
