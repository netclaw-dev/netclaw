## Context

NetClaw already syncs private SkillServer feeds through `ServerFeedSkillSyncService` using the Cloudflare Agent Skills RFC index. That path is intentionally skill-only and writes managed skills under `~/.netclaw/skills/.server-feeds/<feed-name>/` before rebuilding the skill registry.

SkillServer now exposes a native manifest sidecar at `/manifest.json` with native resource traversal and artifact download APIs. The native sidecar is needed for sub-agents because the RFC skill feed cannot represent them. NetClaw's current sub-agent loader only scans top-level `~/.netclaw/agents/*.md`, so a managed server-feed namespace must be introduced without letting feeds overwrite operator-authored local agents.

Actor boundary note: this change stays in daemon background sync and configuration loading. It updates files on disk and the in-memory sub-agent registry, but it does not add new actor messages, session journal events, or persistence schema. Sub-agent execution behavior remains under the existing `SubAgentActor` contract after definitions are loaded.

## Goals / Non-Goals

**Goals:**

- Preserve RFC skill sync as the primary and authoritative skill path.
- Feature-detect native `/manifest.json` per configured SkillServer feed.
- Sync native `agent-md` sub-agent artifacts into a NetClaw-owned managed namespace.
- Verify SHA-256 digests before writing managed sub-agent files.
- Keep local user-authored sub-agents authoritative on name conflicts.
- Prune only managed server-feed sub-agents after a confirmed successful native sync.
- Keep previous managed files during native sidecar outages, malformed responses, timeouts, or verification failures.

**Non-Goals:**

- Replace RFC skill sync with native skill sync.
- Add native sync for non-sub-agent resources in this MVP.
- Add new feed configuration knobs unless implementation proves they are required.
- Let SkillServer manifest data prescribe local filesystem paths.
- Add manifest signature verification.

## Decisions

### D1. RFC index fetch remains the feed reachability gate

For each enabled feed, NetClaw first fetches the RFC skill index with the existing client path. If that fetch times out or fails, the service skips both skill updates and native sidecar sync for that feed. If the RFC index fetch succeeds, even with zero skills, NetClaw may attempt optional native sidecar detection.

Rationale:

- Preserves the current RFC-first mental model and failure behavior.
- Avoids treating native manifest success as a replacement for RFC skill sync.
- Still allows sub-agent-only feeds when the server is reachable and the RFC endpoint responds with an empty index.

Alternative considered:

- Fetch native sidecar even when RFC fetch fails. Rejected because it creates two competing feed reachability models and makes pruning safety harder to reason about during partial outages.

### D2. Native sidecar sync is fail-soft and optional

Missing `/manifest.json`, 404s, malformed native manifests, unsupported native manifest shapes, and native traversal failures are logged and treated as sidecar sync failures only. The existing RFC skill sync result remains valid, and existing managed sub-agent files are left untouched.

Rationale:

- Existing SkillServer feeds and non-SkillServer RFC feeds should continue to work unchanged.
- Native sidecar deployment can roll out independently from NetClaw client support.

Alternative considered:

- Fail the entire feed sync when native sidecar sync fails. Rejected because sub-agent distribution is additive and should not break skill updates.

### D3. NetClaw owns all managed local paths

Server-synced sub-agents are written under `~/.netclaw/agents/.server-feeds/<feed-name>/<agent-name>.md`. NetClaw derives the filename from the logical sub-agent name after validating it is a safe file segment. Manifest-provided paths are ignored for local storage.

Rationale:

- Prevents path traversal and server-controlled writes outside the managed namespace.
- Mirrors the existing managed server-feed skill namespace.
- Makes pruning scope precise.

Alternative considered:

- Allow the native manifest to carry local target paths. Rejected because it gives remote feed content too much authority over the operator's filesystem.

### D4. Downloaded sub-agent files must verify and self-identify

NetClaw downloads the native `agent-md` artifact for each advertised sub-agent, verifies the expected SHA-256 digest, parses the markdown frontmatter, and requires the frontmatter `name` to match the advertised manifest name before replacing the managed file.

Rationale:

- Digest verification protects against corrupted or wrong artifacts.
- Frontmatter identity validation prevents a feed from advertising one agent name while delivering another.
- Reusing the existing markdown parser keeps format behavior aligned with local sub-agent authoring.

Alternative considered:

- Trust manifest metadata without parsing the downloaded file before write. Rejected because the runtime loads the markdown file, so the file's own frontmatter is the authoritative execution input.

### D5. Local user-authored agents take precedence over managed feed agents

The loader scans top-level `~/.netclaw/agents/*.md` as user-owned definitions first, then scans managed server-feed directories. If a managed feed agent has the same logical name as a local user-owned definition, the local definition is registered and the managed one is skipped with a diagnostic. If multiple managed feeds publish the same name, the configured feed order determines the winner and later duplicates are skipped with diagnostics.

Rationale:

- Protects operator intent and local customization.
- Keeps conflict behavior deterministic.
- Keeps managed feed files available on disk for audit and future conflict resolution without exposing the shadowed definition at runtime.

Alternative considered:

- Let the most recently synced managed feed override local files. Rejected because it would make remote feeds capable of changing local operator behavior unexpectedly.

### D6. Pruning is a post-success managed-only operation

Each feed tracks its managed sub-agent sync state separately from user-authored files. After native sidecar traversal and all advertised sub-agent artifact operations for that feed complete successfully, NetClaw prunes managed files and state entries no longer advertised by that feed. If any native sub-agent download, verification, parse, or write fails, the sync is partial and no managed sub-agent pruning occurs for that feed.

Rationale:

- Prevents transient partial failures from deleting still-useful managed agents.
- Keeps destructive behavior confined to the feed-owned managed namespace.

Alternative considered:

- Prune based on whatever subset was successfully downloaded. Rejected because one failed artifact could incorrectly remove other managed agents during an outage or server bug.

## Risks / Trade-offs

- [Risk] The existing sub-agent loader fingerprint only covers top-level files. Mitigation: include managed feed files in the fingerprint so runtime refresh sees server-synced changes.
- [Risk] Managed feed conflicts can be confusing when local definitions win. Mitigation: emit explicit diagnostics with local path, feed name, and shadowed managed path.
- [Risk] Feed names may contain unsafe path characters. Mitigation: reuse existing feed directory behavior only if safe; otherwise add a shared safe-segment helper before writing managed agent paths.
- [Risk] Native sidecar sync adds network calls to startup feed sync. Mitigation: reuse per-feed timeout bounds and keep sidecar failures fail-soft.
- [Risk] Partial native sync may write some updated agents while retaining stale ones and skipping prune. Mitigation: log partial sync status and retry on the next scheduled sync.

## Migration Plan

1. Upgrade NetClaw's `Netclaw.SkillClient` dependency to the published prerelease containing native manifest APIs.
2. Add NetClaw path helpers for managed server-feed sub-agent directories and sync-state path.
3. Extend `ServerFeedSkillSyncService` with optional native sidecar discovery after successful RFC index fetch.
4. Implement verified native sub-agent download, validation, atomic managed-file replacement, state updates, and safe pruning.
5. Extend `FileSubAgentDefinitionLoader` to scan local top-level agents first and managed feed agents second with deterministic conflict diagnostics.
6. Add targeted tests for sidecar absence, sidecar success, digest failure, partial sync no-prune, local precedence, and managed prune behavior.
7. Update docs if operator-facing feed/sub-agent sync behavior needs to be documented.

Rollback:

- Disable or remove the native sidecar branch from `ServerFeedSkillSyncService`; existing RFC skill sync remains intact.
- Managed sub-agent files under `~/.netclaw/agents/.server-feeds/` can remain on disk but will no longer be refreshed or loaded if the loader change is also reverted.
- No journal or database rollback is required.

## Open Questions

- Should shadowed managed sub-agents appear in diagnostic tooling beyond daemon logs?
- Should stale managed sub-agent files be kept for audit instead of deleted when pruned?
- Should `SkillSync.Enabled = false` also disable managed sub-agent server-feed loading, even if files already exist on disk?
