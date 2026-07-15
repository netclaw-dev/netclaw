# audience-context-filtering Specification

## Purpose

Netclaw sessions can be addressed by different trust audiences within the same
runtime — Public (untrusted external requesters), Team (trusted operators), and
Personal (the owner). Context assembled for the model, file-access behavior, and
error messaging MUST respect the audience of the active turn so that internal
information (skill/memory/subagent indexes, filesystem paths, working context,
allowed file roots) is never surfaced to a lower-trust audience. This capability
defines how the audience parameter flows through context-layer assembly, session
and working-context blocks, file-access denial messaging, implicit file roots,
and audience derivation, with secure-by-default behavior and no default-audience
fallback.
## Requirements
### Requirement: Context layer audience filtering

The context layer system SHALL accept a `TrustAudience` parameter on
`IContextLayerProvider.GetContextLayer()`. Each context layer implementation
SHALL use the audience to determine what content to return. The
`ContextAssemblyInput` record SHALL include a `TrustAudience Audience` field.
When a feature is disabled deployment-wide, the corresponding context layer
SHALL also return empty even for non-Public audiences.

#### Scenario: Public audience receives no skill index

- **WHEN** a Public-audience session assembles context
- **THEN** `SkillIndexContextLayer.GetContextLayer(Public)` returns empty string
- **AND** no skill index appears in the session's system messages

#### Scenario: Public audience receives no memory index

- **WHEN** a Public-audience session assembles context
- **THEN** `MemoryIndexContextLayer.GetContextLayer(Public)` returns empty string
- **AND** no memory tool hints appear in the session's system messages

#### Scenario: Public audience receives no subagent discovery

- **WHEN** a Public-audience session assembles context
- **THEN** `SubAgentDiscoveryContextLayer.GetContextLayer(Public)` returns empty string
- **AND** no subagent index appears in the session's system messages

#### Scenario: Disabled skills feature suppresses skill index for Team

- **GIVEN** `SkillSync.Enabled` is `false` in config
- **WHEN** a Team-audience session assembles context
- **THEN** `SkillIndexContextLayer.GetContextLayer(Team)` returns empty string
- **AND** no skill index appears in the session's system messages

#### Scenario: Team audience receives all allowed context layers

- **WHEN** a Team-audience session assembles context
- **THEN** all enabled context layers return their full content

#### Scenario: Personal audience receives all allowed context layers

- **WHEN** a Personal-audience session assembles context
- **THEN** all enabled context layers return their full content

### Requirement: Session block path redaction

The session block injected into system messages SHALL omit filesystem paths
for Public-audience sessions. The session ID SHALL remain visible for all
audiences.

#### Scenario: Public session block contains ID only

- **WHEN** a Public-audience session assembles the static context block
- **THEN** the session block contains `[session]\nid: {sessionId}`
- **AND** no `session_dir` or `media_dir` lines are present

#### Scenario: Team session block contains full paths

- **WHEN** a Team-audience session assembles the static context block
- **THEN** the session block contains `id`, `session_dir`, and `media_dir`

### Requirement: Working context suppression for Public

The working context block, including project directory, recent files, Git worktree paths, branch, HEAD, and dirty state, SHALL NOT be injected into Public-audience main sessions or subagents. Public audience eligibility SHALL be decided before Git inspection, so Public turns SHALL NOT start a Git process for working-context enrichment.

#### Scenario: Public session has no working context

- **WHEN** a Public-audience session has a non-empty working context or eligible Git project directory
- **THEN** no `[working-context]` block is injected into the volatile context block
- **AND** Git inspection is not invoked

#### Scenario: Public subagent receives no internal working context

- **GIVEN** a subagent is launched under a Public parent turn
- **WHEN** the child initial prompt is assembled
- **THEN** no parent project path, recent-file list, or Git state is included
- **AND** Git inspection is not invoked for the child snapshot

#### Scenario: Team session receives working context

- **WHEN** a Team-audience session has a non-empty working context
- **THEN** `WorkingContext` and any successfully derived Git enrichment are injected into the volatile context block

### Requirement: File access error message sanitization

File access denial messages for Public-audience sessions SHALL NOT include
the list of allowed root paths or mention the session directory as an allowed
root. Team and Personal audiences SHALL continue to receive verbose error
messages including allowed roots.

#### Scenario: Public file access denial omits roots

- **WHEN** a Public-audience session attempts to read a file outside allowed roots
- **THEN** the error message does not reveal any allowed root
- **AND** no root paths are listed in the error
- **AND** the session directory is not named or implied in the error

#### Scenario: Team file access denial includes roots

- **WHEN** a Team-audience session attempts to read a file outside allowed roots
- **THEN** the error message includes the list of allowed root paths

### Requirement: Public audience has no implicit internal file roots

Public file access SHALL NOT implicitly include identity, skills, or workspaces
roots through global/default file-root configuration.

#### Scenario: Public file access is session-scoped only

- **GIVEN** a Public-audience session with default file access configuration
- **WHEN** it resolves implicit readable roots
- **THEN** the resolved roots include only session-scoped locations
- **AND** identity, skills, and workspaces roots are absent unless explicitly
  configured for a non-Public audience

### Requirement: Audience derivation has no default-audience fallback

The session pipeline SHALL derive a turn's audience only from the explicitly
supplied turn source. There SHALL be no pipeline-level `DefaultAudience`,
`DefaultBoundary`, `DefaultPrincipal`, or `DefaultProvenance` configuration
property. A turn that reaches audience derivation without a turn source SHALL
fail loudly rather than adopt a default audience.

#### Scenario: No default-audience configuration exists

- **WHEN** session pipeline options are constructed
- **THEN** there is no `DefaultAudience` (or sibling `Default*` trust) property
  to set
- **AND** trust context can only enter the pipeline by way of an inbound
  `ChannelInput`

#### Scenario: Audience derivation uses the supplied turn source

- **GIVEN** a turn with an explicit turn source carrying `TrustAudience.Personal`
- **WHEN** the pipeline derives the effective audience
- **THEN** the derived audience reflects the Personal source audience
- **AND** no default-audience value participates in the derivation
