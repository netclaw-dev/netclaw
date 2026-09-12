## ADDED Requirements

### Requirement: Managed plugin source contract

The system SHALL store public GitHub plugin sources under the existing external skill source configuration.
Each source SHALL have a stable operator source ID that is separate from the package manifest name.

The source SHALL contain a canonical repository, package format, optional subdirectory, resolved branch or commit, enabled state, and timeout.
The configuration schema SHALL reject unknown or invalid source properties.

#### Scenario: Valid source persists in canonical form

- **GIVEN** an operator supplies a canonical public GitHub repository and a valid source ID
- **WHEN** the daemon accepts the install request
- **THEN** the daemon persists one canonical managed plugin source
- **AND** the runtime source validator accepts the persisted representation

#### Scenario: Invalid source fails before persistence

- **GIVEN** an install request has an unsafe source ID, path, reference, format, or repository
- **WHEN** the daemon validates the request
- **THEN** the daemon returns a safe validation error
- **AND** the configuration remains byte-identical

### Requirement: Deterministic package format selection

The system SHALL support `auto`, `agent-plugin`, and `codex` package formats.
New managed sources SHALL default to `auto`.

Auto selection SHALL prefer a recognized Agent Plugins root manifest over a Codex compatibility manifest.
An explicit format SHALL inspect only its selected manifest.
The system SHALL NOT fall through to another format after it selects a manifest that fails validation.

#### Scenario: Auto selects the portable root

- **GIVEN** an archive has a valid Agent Plugins root manifest and a Codex compatibility manifest
- **WHEN** the source uses `auto`
- **THEN** the selector uses only the Agent Plugins root manifest
- **AND** it does not merge Codex declarations

#### Scenario: Invalid selected root does not fall through

- **GIVEN** an archive has a recognized but invalid Agent Plugins root manifest
- **AND** the archive has a valid Codex compatibility manifest
- **WHEN** the source uses `auto`
- **THEN** the candidate fails with the portable manifest error
- **AND** the selector does not use the Codex manifest

### Requirement: Agent Plugins manifest compliance

The portable adapter SHALL require root `plugin.json` with the recognized Agent Plugins 1.0.0 schema.
It SHALL apply the closed manifest rules and the standard non-fatal exceptions.

The adapter SHALL accept a portable name with lowercase letters, numbers, hyphens, and periods.
It SHALL treat `version` as optional string metadata and SHALL NOT require SemVer.
It SHALL ignore supported `extensions` entries that Netclaw does not implement.

#### Scenario: Portable package with a dotted name loads

- **GIVEN** a root manifest declares the recognized schema and name `acme.tools`
- **AND** its `skills/` directory contains one valid skill
- **WHEN** the portable adapter inspects the package
- **THEN** the adapter accepts `acme.tools` as the plugin name
- **AND** it selects the valid skill

#### Scenario: Non-SemVer version remains valid

- **GIVEN** a valid portable manifest declares version `release-2026-09`
- **WHEN** the portable adapter validates the manifest
- **THEN** the adapter accepts the version string
- **AND** the update policy compares it as exact metadata

#### Scenario: Invalid required manifest field rejects the plugin

- **GIVEN** a root manifest lacks the recognized schema or a valid name
- **WHEN** the portable adapter validates the manifest
- **THEN** the adapter rejects the plugin candidate
- **AND** no package file reaches the active inventory

### Requirement: Portable component failure isolation

The portable adapter SHALL discover skills only under immediate children of `skills/`.
It SHALL skip an invalid discovered skill and continue with valid sibling skills.

The adapter SHALL report unsupported or invalid component types without activating them.
A portable package with no supported components SHALL remain a valid installed package.
A content scanner security rejection SHALL reject the complete candidate.

#### Scenario: Invalid skill does not block a valid sibling

- **GIVEN** a portable package has one valid skill and one invalid skill
- **WHEN** the daemon builds the candidate
- **THEN** the candidate includes the valid skill
- **AND** the result reports the skipped invalid skill

#### Scenario: Unsupported MCP component does not activate

- **GIVEN** a portable package has valid skills and an `mcp.json` file
- **WHEN** Netclaw imports the package
- **THEN** Netclaw publishes the valid skills
- **AND** Netclaw reports that MCP activation is unsupported
- **AND** Netclaw does not start an MCP process or connection

#### Scenario: Scanner security rejection blocks publication

- **GIVEN** the scanner rejects one selected skill or text resource
- **WHEN** the daemon builds the candidate
- **THEN** the daemon rejects the complete candidate
- **AND** the previous complete package remains active

### Requirement: Codex compatibility isolation

The Codex adapter SHALL read `.codex-plugin/plugin.json` only.
It SHALL preserve the existing declared skill-root behavior and whole-candidate syntax validation.

Codex-only fields SHALL NOT change portable Agent Plugins behavior.
Executable component declarations SHALL NOT grant runtime authority.

#### Scenario: Explicit Codex format reads only the compatibility manifest

- **GIVEN** an archive has a valid Codex compatibility manifest
- **WHEN** the source selects `codex`
- **THEN** the Codex adapter selects only its declared skill roots
- **AND** it does not require a portable root manifest

#### Scenario: Codex executable declaration grants no authority

- **GIVEN** a Codex manifest declares a hook, command, agent, or MCP server
- **WHEN** Netclaw imports the package
- **THEN** Netclaw does not activate that declaration
- **AND** the result reports the excluded component

### Requirement: Secure Git package acquisition

The daemon SHALL acquire packages only from canonical public GitHub HTTPS sources.
It SHALL keep the existing redirect, archive, entry, path, link, file type, count, and byte limits.

The daemon SHALL extract only package files that the selected adapter authorizes.
It SHALL NOT execute hooks, filters, submodules, installers, scripts, or package files during acquisition.

#### Scenario: Safe archive produces a bounded candidate

- **GIVEN** a supported package stays within every acquisition limit
- **WHEN** the daemon acquires its resolved commit
- **THEN** the daemon extracts only the selected skill trees
- **AND** it seals one immutable candidate directory

#### Scenario: Escaped or special entry rejects the candidate

- **GIVEN** an archive contains traversal, a link, a special file, or an escaped selected path
- **WHEN** the daemon inspects the archive
- **THEN** the daemon rejects the candidate before publication
- **AND** it does not write outside the managed plugin root

### Requirement: Durable update and recovery policy

The system SHALL keep the installed version, exact commit, observed commit, source fingerprint, package name, and rejection state in durable storage.
An exact equal declared version SHALL suppress an update.
An absent version SHALL use commit identity for update eligibility.

Branch sources SHALL follow their configured branch.
Tag and commit sources SHALL remain pinned to the resolved commit.
The previous immutable package SHALL remain active after a failed update.

#### Scenario: Equal version suppresses a new commit

- **GIVEN** the installed receipt has version `1.2.0`
- **AND** a new commit declares version `1.2.0`
- **WHEN** an ordinary sync runs
- **THEN** the installed package remains unchanged
- **AND** the receipt records the new observed commit

#### Scenario: Failed update preserves the prior package

- **GIVEN** a source has a complete installed package
- **AND** its next candidate fails validation or a security scan
- **WHEN** sync processes that candidate
- **THEN** the prior immutable package remains active
- **AND** the daemon records the rejected commit when the failure is deterministic

### Requirement: Single coordinated publication path

The existing external sync actor SHALL own the timer, active pass, waiter set, and lifetime token.
A managed plugin sync participant SHALL own plugin startup publication, source updates, durable rejections, alerts, and cleanup.

The external sync coordinator SHALL refresh the complete skill inventory once after source participants finish.
Concurrent readers SHALL see one complete previous or new inventory snapshot.

#### Scenario: One plugin failure does not block another source

- **GIVEN** one managed plugin fails and another external source succeeds
- **WHEN** one external sync pass runs
- **THEN** the pass reports both source results
- **AND** the successful source reaches the final inventory refresh

#### Scenario: Concurrent caller joins the active pass

- **GIVEN** a managed plugin update pass is active
- **WHEN** another authenticated caller requests an external sync
- **THEN** the actor adds the caller to the active waiter set
- **AND** both callers receive the same pass ID

### Requirement: Plugin skill authority remains unchanged

Managed plugin skills SHALL enter the existing logical skill inventory.
The existing audience policy, skill disable state, path policy, and logical resource tools SHALL control access.

Package metadata SHALL NOT grant tool, subagent, shell, MCP, or filesystem authority.
Managed package files SHALL remain protected from agent writes.

#### Scenario: Accepted skill uses logical access

- **GIVEN** a managed plugin publishes an accepted skill and resource
- **WHEN** an authorized session uses `skill_load` and `skill_read_resource`
- **THEN** the runtime resolves both through the logical skill name
- **AND** it does not expose the managed physical root

#### Scenario: Manifest tool field cannot grant a tool

- **GIVEN** a compatibility manifest or skill header names a Netclaw tool
- **WHEN** the package enters the inventory
- **THEN** the current audience tool policy remains authoritative
- **AND** the package does not broaden tool exposure or execution authority
