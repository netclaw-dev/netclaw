## Why

The Teams configuration TUI makes saved access rules hard to inspect and remove.
It also lacks a safe friendly Group Chat discovery path.

This change makes destinations and principal rules clear, scoped, and removable.
It adds optional Group Chat metadata discovery without changing the existing ingress contract.

## What Changes

- Replace the Teams management menu with clear add and manage paths for destinations and principals.
- Show saved channels, Group Chats, users, and groups before a directory operation starts.
- Add exact, scoped removal and validation for new manual principal IDs.
- Make directory search request-owned and loop-owned after asynchronous completion.
- Add app-only, selected-user Group Chat discovery with bounded paging and opaque continuations.
- Keep Group Chat metadata consent optional and separate from existing Teams message ingress.
- Preserve canonical IDs, global mention policy, additive channel rules, and existing credential storage.
- Add TUI, Graph, configuration-to-runtime, and approval authorization regression coverage.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `teams-directory-management`: Add bounded selected-user Group Chat metadata discovery and scoped opaque continuation handling.
- `teams-principal-authorization`: Preserve exact global and channel principal removal semantics through configuration activation and approval callbacks.
- `teams-configuration-tui`: Define accessible add, manage, filter, inspect, validation, and removal flows for Teams destinations and principals.
- `microsoft-teams-channel`: Define the optional Group Chat discovery permission and its separation from Group Chat ingress.

## Impact

The change affects the Teams TUI, its directory contracts, Graph adapter, configuration writer, diagnostics, tests, and operator guide.

The Graph adapter adds optional `Chat.ReadBasic.All` use for selected-user chat metadata only.
The app does not add tenant enumeration, message history, installation mutation, or a second credential.

The daemon retains the current canonical configuration and authorization rules.
