## Why

The Teams channel does not support group chats or safe inbound attachments.
Operators cannot use Teams group conversations or share supported files with Netclaw.

This final Phase-1 slice completes the remaining Teams channel scope after Proxicon PR #53.
It maps to upstream references `netclaw-dev/netclaw#1401` and `netclaw-dev/netclaw#1946`.

## What Changes

- Add `GroupChat` as a distinct Teams conversation scope and canonical session identity.
- Accept group-chat ingress only after tenant, opt-in, canonical chat, principal, and mention checks succeed.
- Reuse Teams principal authorization for group-chat senders. Channel overrides do not grant group-chat access.
- Add group-chat reply, typing, approval, reminder, recovery, and duplicate-delivery support through the established Teams architecture.
- Add a disabled-by-default Teams attachment option.
- Accept authenticated inline images through the shared media pipeline in supported Teams conversation scopes.
- Accept supported Personal file attachments through bounded Teams downloads and shared managed attachment storage.
- Reject channel and group-chat files when a secure bounded retrieval path is not proven without broad permissions.
- Add the required Teams package scopes, resource-specific permissions, and file declaration.
- Add Teams configuration, TUI, documentation, doctor, deterministic tests, and owner smoke steps.

No shared approval, session, tool, ACL, or non-Teams channel behavior changes.

## Capabilities

### New Capabilities

- `microsoft-teams-channel`: Secure Teams group-chat routing, attachment ingress, package permissions, configuration, and operator controls.

### Modified Capabilities

None.

## Impact

Source PRDs: `PRD-002`, `PRD-004`, and `PRD-009`.

The change affects the Teams channel and Graph projects, the daemon Teams SDK boundary,
Teams configuration and TUI paths, the Teams package, Teams tests, and Teams documentation.

Security impact:

- Group-chat and attachment ingress default to disabled.
- Canonical IDs remain the authority. Friendly labels remain display metadata.
- The channel uses no tenant-wide chat or file permission.
- Download URLs, SDK attachment types, and tokens stay at the daemon transport boundary.
- Shared scanners, MIME validation, image normalization, and untrusted attachment semantics remain authoritative.

Operational impact:

- Operators must upgrade or install the Teams package in each target group chat for RSC consent.
- The TUI supports canonical group-chat IDs when Graph cannot provide a friendly label.
- The operator must run the documented tenant smoke plan after package upgrade.
- Proxicon has GitHub Issues disabled, so the required focused issue cannot be created at this time.
