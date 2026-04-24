## MODIFIED Requirements

### Requirement: Session recovery across restart

Session actors SHALL recover their full state from the event journal (with
optional snapshot acceleration). Recovery includes conversation history, turn
count, title, working context (including recent files and project directory),
and processed reminder IDs. After recovery, the system prompt SHALL be loaded
fresh from disk — including project-scoped identity files discovered via the
recovered project directory. When identity files are missing, the last-known
system prompt from recovery SHALL be retained rather than deleted.

#### Scenario: Recover state from journal

- **GIVEN** a session with prior persisted turns
- **WHEN** the session actor restarts
- **THEN** state is rebuilt from journal events (or snapshot + delta)
- **AND** the system prompt is loaded fresh from identity files
- **AND** `WorkingContext.ProjectDirectory` is restored from the snapshot

#### Scenario: Recovery with missing identity files retains last-known prompt

- **GIVEN** a session with prior persisted turns and a system prompt
- **WHEN** the session actor restarts and identity files are not found on disk
- **THEN** the last-known system prompt from recovery is retained
- **AND** a warning is logged

#### Scenario: Recovery with project directory loads project instructions

- **GIVEN** a session with project directory set to
  `/home/user/workspaces/akadonic`
- **WHEN** the session actor recovers and
  `/home/user/workspaces/akadonic/CLAUDE.md` exists
- **THEN** the `[project-instructions]` block is populated from the
  project's identity file on the first LLM call

## ADDED Requirements

### Requirement: Working context project directory field

`WorkingContext` SHALL include a `ProjectDirectory` field (protobuf tag 2)
that tracks which project the session is working on. The field SHALL be
nullable for backward compatibility. `WorkingContext.IsEmpty` SHALL return
false when a project directory is set even if `RecentFiles` is empty.

#### Scenario: WorkingContext serialization round-trip with project directory

- **GIVEN** a `WorkingContext` with project directory
  `/home/user/workspaces/akadonic` and recent files
- **WHEN** serialized to protobuf and deserialized
- **THEN** the deserialized instance has the same project directory and
  recent files

#### Scenario: Legacy WorkingContext without project directory deserializes to null

- **GIVEN** a serialized `WorkingContext` from before project directory
  tracking was added
- **WHEN** deserialized
- **THEN** `ProjectDirectory` is null
- **AND** `RecentFiles` is preserved

#### Scenario: IsEmpty reflects project directory presence

- **GIVEN** a `WorkingContext` with project directory set but no recent files
- **WHEN** `IsEmpty` is checked
- **THEN** it returns false
