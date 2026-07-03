## Why

SkillServer now exposes native manifest endpoints for resources that the Cloudflare Agent Skills RFC feed cannot represent, especially sub-agents. NetClaw currently consumes only the RFC skill index, so private SkillServer feeds can distribute skills but cannot safely distribute companion sub-agent definitions needed by `metadata.subagent` routing.

## What Changes

- Keep RFC skill sync as the authoritative primary path for skills.
- Feature-detect each configured SkillServer feed's optional native `/manifest.json` sidecar after RFC sync remains available.
- Sync native sub-agent definitions from the sidecar into a feed-owned managed namespace under `~/.netclaw/agents/.server-feeds/<feed-name>/`.
- Verify downloaded sub-agent artifacts by SHA-256 before writing them to disk.
- Preserve existing local user-authored sub-agent files and give them precedence on name conflicts.
- Prune only managed server-synced sub-agent files, and only after a confirmed successful native sidecar sync.
- Keep previous managed sub-agent files when the native sidecar is unavailable, malformed, times out, or fails artifact verification.

## Capabilities

### New Capabilities

- `skillserver-native-sidecar-sync`: Defines optional native manifest sidecar discovery, native-only resource sync, managed storage, digest verification, and safe pruning semantics for SkillServer feeds.

### Modified Capabilities

- `netclaw-subagents`: Add managed server-feed sub-agent discovery, local-user precedence, and conflict diagnostics to the sub-agent loading contract.

## Impact

### Affected code and systems

- `ServerFeedSkillSyncService` will continue using the RFC index for skills and add optional native manifest traversal for sub-agents.
- `Netclaw.SkillClient` package consumption will move to the prerelease client version that contains native manifest and sub-agent artifact APIs.
- `NetclawPaths` will need a managed sub-agent feed namespace alongside the existing user-authored `AgentsDirectory`.
- `FileSubAgentDefinitionLoader` and `SubAgentDefinitionRegistry` will need deterministic loading and conflict handling across user-authored and managed feed files.
- Daemon tests will need coverage for native sidecar success, unavailable sidecar fallback, digest failure, user precedence, and prune safety.

### APIs and behavior

- No public user-facing CLI or config breaking change is intended.
- Existing SkillServer feeds without `/manifest.json` continue to sync skills exactly as today.
- Existing local sub-agent files remain user-owned and are never overwritten or deleted by server-feed sync.

### Security and operational impact

- Server manifest metadata SHALL NOT prescribe local filesystem paths; NetClaw maps names to its own managed namespace.
- Artifact digests are verified before writes; failed verification keeps the previous managed file, if any.
- Sub-agent sync is fail-soft relative to skill sync so a native sidecar outage cannot remove existing skills or agents.
- Conflict diagnostics must make it clear when a server-managed sub-agent is shadowed by a local user-authored definition.

### In scope for MVP

- Optional `/manifest.json` feature detection.
- Native sub-agent traversal and `agent-md` artifact download.
- SHA-256 verification and atomic managed-file replacement.
- Managed sub-agent load support and local precedence.
- Safe managed sub-agent pruning after successful sync.
- Unit tests and targeted daemon/configuration tests for the new sync behavior.

### Out of scope for MVP

- Replacing RFC skill sync with native skill sync.
- Syncing non-sub-agent native resources.
- Letting SkillServer choose NetClaw local paths.
- Overwriting or deleting user-authored local sub-agents.
- Signature verification for native manifests beyond the existing digest verification requirement.

### Source PRDs

- `PRD-001` (MVP runtime determinism and reliability)
- `PRD-002` (security envelope and fail-closed/default-deny posture)
- `PRD-004` (operator configuration and local filesystem ownership)
