## ADDED Requirements

### Requirement: Context layer audience filtering

The context layer system SHALL accept a `TrustAudience` parameter on
`IContextLayerProvider.GetContextLayer()`. Each context layer implementation
SHALL use the audience to determine what content to return. The
`ContextAssemblyInput` record SHALL include a `TrustAudience Audience` field.

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

#### Scenario: Team audience receives all context layers

- **WHEN** a Team-audience session assembles context
- **THEN** all context layers return their full content
- **AND** skill index, memory index, and subagent discovery are included

#### Scenario: Personal audience receives all context layers

- **WHEN** a Personal-audience session assembles context
- **THEN** all context layers return their full content

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

### Requirement: Working context suppression for public

The working context block (project directory, recent files) SHALL NOT be
injected into Public-audience sessions.

#### Scenario: Public session has no working context

- **WHEN** a Public-audience session has a non-empty working context
- **THEN** `WorkingContext.ToContextBlock()` is NOT injected into the volatile context block

#### Scenario: Team session receives working context

- **WHEN** a Team-audience session has a non-empty working context
- **THEN** `WorkingContext.ToContextBlock()` IS injected into the volatile context block

### Requirement: File access error message sanitization

File access denial messages for Public-audience sessions SHALL NOT include
the list of allowed root paths. Team and Personal audiences SHALL continue
to receive verbose error messages including allowed roots.

#### Scenario: Public file access denial omits roots

- **WHEN** a Public-audience session attempts to read a file outside allowed roots
- **THEN** the error message says "Public trust context may only access files inside the current session directory."
- **AND** no root paths are listed in the error

#### Scenario: Team file access denial includes roots

- **WHEN** a Team-audience session attempts to read a file outside allowed roots
- **THEN** the error message includes the list of allowed root paths
