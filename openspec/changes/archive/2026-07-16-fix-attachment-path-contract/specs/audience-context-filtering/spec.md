## MODIFIED Requirements

### Requirement: Session block path redaction

The session block injected into system messages SHALL omit filesystem paths for Public-audience sessions. The session ID SHALL remain visible for all audiences. Team and Personal sessions SHALL receive `session_dir` as the single agent-facing filesystem root and SHALL NOT receive the internal model-media directory as a separate path.

#### Scenario: Public session block contains ID only

- **WHEN** a Public-audience session assembles the static context block
- **THEN** the session block contains `[session]\nid: {sessionId}`
- **AND** no `session_dir` or `media_dir` lines are present

#### Scenario: Team session block contains authoritative session root

- **WHEN** a Team-audience session assembles the static context block
- **THEN** the session block contains `id` and `session_dir`
- **AND** no `media_dir` line is present

#### Scenario: Personal session block contains authoritative session root

- **WHEN** a Personal-audience session assembles the static context block
- **THEN** the session block contains `id` and `session_dir`
- **AND** no `media_dir` line is present
