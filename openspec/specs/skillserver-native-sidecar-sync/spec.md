# skillserver-native-sidecar-sync Specification

## Purpose

Define how Netclaw consumes a SkillServer feed's optional native
`/manifest.json` sidecar. The Cloudflare Agent Skills RFC index stays the
primary skill source. The sidecar adds native-only resources, such as sub-agent
definitions, into a feed-owned managed namespace. Sidecar failure is
non-destructive.

## Requirements

### Requirement: RFC skill sync remains primary with optional native sidecar

For each enabled SkillServer feed, the system SHALL keep using the Cloudflare Agent Skills RFC index as the primary skill sync source. After a successful RFC index fetch for a feed, including a successful empty index, the system MAY feature-detect that feed's native `/manifest.json` sidecar for native-only resources. Native sidecar absence or failure SHALL NOT cause RFC skill sync for that feed to fail.

#### Scenario: Feed without native sidecar still syncs skills

- **GIVEN** an enabled server feed exposes a valid RFC skill index
- **AND** the feed does not expose `/manifest.json`
- **WHEN** server feed sync runs
- **THEN** NetClaw syncs skills from the RFC index using existing behavior
- **AND** logs or records that native sidecar sync was unavailable
- **AND** leaves existing managed sub-agent files for that feed unchanged

#### Scenario: RFC fetch failure skips native sidecar sync

- **GIVEN** an enabled server feed times out or fails while fetching the RFC skill index
- **WHEN** server feed sync runs
- **THEN** NetClaw does not attempt native sidecar sync for that feed
- **AND** keeps existing on-disk skills and managed sub-agents for that feed

#### Scenario: Empty RFC index can still use native sidecar

- **GIVEN** an enabled server feed returns a successful RFC index with zero skills
- **AND** the feed exposes a valid native sidecar with sub-agents
- **WHEN** server feed sync runs
- **THEN** NetClaw does not create or prune RFC skills for that empty index beyond existing safe behavior
- **AND** may sync native sub-agents from the sidecar

### Requirement: Native sub-agent artifacts sync to a managed namespace

When a feed's native sidecar advertises sub-agent resources, the system SHALL traverse native sub-agent versions, select the `agent-md` artifact, download it, verify its SHA-256 digest, and write it only to a NetClaw-owned managed path under `~/.netclaw/agents/.server-feeds/<feed-name>/<agent-name>.md`. Server-provided manifest metadata SHALL NOT control local filesystem paths.

#### Scenario: Verified sub-agent artifact is written atomically

- **GIVEN** a native sidecar advertises sub-agent `code-reviewer` with an `agent-md` artifact and expected SHA-256 digest
- **AND** the downloaded artifact content hashes to the expected digest
- **AND** the artifact frontmatter declares `name: code-reviewer`
- **WHEN** native sidecar sync processes the sub-agent
- **THEN** NetClaw writes the file to `~/.netclaw/agents/.server-feeds/<feed-name>/code-reviewer.md`
- **AND** replaces any previous managed file atomically
- **AND** records sync state for the managed sub-agent

#### Scenario: Server path metadata is ignored

- **GIVEN** a native sidecar artifact includes metadata that resembles an absolute path or relative traversal path
- **WHEN** native sidecar sync processes the artifact
- **THEN** NetClaw ignores that metadata for local storage
- **AND** derives the managed target path only from the configured feed name and validated sub-agent name

#### Scenario: Digest mismatch keeps previous managed file

- **GIVEN** a native sidecar advertises sub-agent `code-reviewer`
- **AND** a previous managed file already exists for `code-reviewer`
- **WHEN** the downloaded artifact hash does not match the expected SHA-256 digest
- **THEN** NetClaw rejects the downloaded artifact
- **AND** keeps the previous managed file unchanged
- **AND** treats the feed's native sidecar sync as partial for pruning purposes

#### Scenario: Artifact name mismatch is rejected

- **GIVEN** a native sidecar advertises sub-agent `code-reviewer`
- **AND** the downloaded artifact frontmatter declares `name: other-agent`
- **WHEN** native sidecar sync processes the artifact
- **THEN** NetClaw rejects the downloaded artifact
- **AND** does not replace the managed `code-reviewer.md` file

### Requirement: Native sidecar failure preserves managed sub-agents

The system SHALL treat native sidecar failures as non-destructive. If native manifest fetch, traversal, artifact download, digest verification, parsing, validation, or managed write fails for a feed, NetClaw SHALL keep existing managed sub-agent files for that feed and SHALL NOT prune removed sub-agents during that sync attempt.

#### Scenario: Malformed native manifest does not prune managed sub-agents

- **GIVEN** managed sub-agent files already exist for a feed
- **WHEN** `/manifest.json` is present but malformed or unsupported
- **THEN** NetClaw logs the native sidecar failure
- **AND** leaves all managed sub-agent files for that feed unchanged
- **AND** does not prune managed sub-agent sync state for that feed

#### Scenario: One failed artifact prevents pruning

- **GIVEN** a native sidecar advertises sub-agents `alpha` and `beta`
- **AND** `alpha` downloads and verifies successfully
- **AND** `beta` fails download or verification
- **WHEN** native sidecar sync completes for the feed
- **THEN** NetClaw may keep the successfully synced `alpha` managed file
- **AND** keeps any previous managed `beta` file unchanged
- **AND** skips pruning for that feed because the sync was partial

### Requirement: Managed sub-agent pruning is successful-sync only

After a native sidecar sync for a feed completes successfully for all advertised sub-agents, the system SHALL remove only managed sub-agent files and sync-state entries for that same feed that are no longer advertised by the sidecar. The system SHALL NOT remove user-authored sub-agents or managed sub-agents belonging to other feeds.

#### Scenario: Removed managed sub-agent is pruned after successful sync

- **GIVEN** the managed namespace for feed `team` contains `old-agent.md` from a previous successful sync
- **AND** the feed's current native sidecar successfully syncs all advertised sub-agents
- **AND** the sidecar no longer advertises `old-agent`
- **WHEN** native sidecar sync completes
- **THEN** NetClaw removes `~/.netclaw/agents/.server-feeds/team/old-agent.md`
- **AND** removes `old-agent` from that feed's managed sub-agent sync state

#### Scenario: User-authored local sub-agent is never pruned

- **GIVEN** `~/.netclaw/agents/code-reviewer.md` exists as a user-authored local sub-agent
- **AND** a native sidecar sync for feed `team` completes successfully
- **WHEN** NetClaw prunes removed managed sub-agents for feed `team`
- **THEN** `~/.netclaw/agents/code-reviewer.md` remains unchanged
- **AND** pruning is limited to `~/.netclaw/agents/.server-feeds/team/`

#### Scenario: Other feed managed sub-agent is never pruned

- **GIVEN** `~/.netclaw/agents/.server-feeds/team-a/reviewer.md` exists
- **AND** `~/.netclaw/agents/.server-feeds/team-b/reviewer.md` exists
- **WHEN** native sidecar sync for feed `team-a` completes and prunes removed entries
- **THEN** NetClaw does not remove or modify files under `~/.netclaw/agents/.server-feeds/team-b/`
