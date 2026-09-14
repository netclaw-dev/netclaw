## MODIFIED Requirements

The terms in this requirement use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: Relative first-party filesystem paths use session-owned bases

`session-cwd` SHALL select the declared project directory when that base is
available. Otherwise, it SHALL select the immutable session directory. If
neither base is available, it SHALL return an `invalid_context` correction. It
SHALL NOT use the daemon process current directory.

`session-cwd` SHALL pass the selected base and authored path to the filesystem
authorization contract that `netclaw-tools` owns. It SHALL NOT make another
path-safety decision. It SHALL NOT retry another base after the path access
decision denies the request.

Resolution examples and counterexamples:

| Authored path and context | Selected path | Result |
|---|---|---|
| `src/App.cs`, project `/workspace/project` | `/workspace/project/src/App.cs` | Apply normal read or write policy. |
| `notes.md`, no project, session `/session/current` | `/session/current/notes.md` | Apply normal session policy. |
| `notes.md`, stale unavailable project, valid session | `/session/current/notes.md` | Fall back before authorization begins. |
| `notes.md`, project has a link ancestor | project | Path access denies it; do not try the session base. |
| `../../outside.txt`, valid project | project | Path access decides on the canonical path; do not try the session base after denial. |
| `notes.md`, no project or session | none | `invalid_context`; do not use the daemon current directory. |
| `/absolute/report.md` | `/absolute/report.md` | Do not select a relative base; apply normal policy directly. |

#### Scenario: Relative read uses declared project

- **GIVEN** a project directory `/workspace/project` and session `/session/current`
- **WHEN** `file_read` receives `src/App.cs`
- **THEN** it authorizes and reads `/workspace/project/src/App.cs`
- **AND** it does not use the daemon current directory

#### Scenario: Relative write falls back to the session directory

- **GIVEN** no declared project and session directory `/session/current`
- **WHEN** `file_write` receives `notes/result.md`
- **THEN** it resolves `/session/current/notes/result.md`
- **AND** the existing session write policy decides authorization

#### Scenario: Traversal receives no implicit authority

- **GIVEN** project directory `/workspace/project`
- **WHEN** a file tool receives `../../outside.txt`
- **THEN** `session-cwd` passes the selected base and authored path to `netclaw-tools`
- **AND** the path access decision denies a path outside the trusted roots

#### Scenario: Ancestor link rejects the relative base

- **GIVEN** a trusted root contains a link ancestor for the declared project
- **WHEN** a file tool receives a relative path
- **THEN** the `netclaw-tools` path access decision returns `access_denied`
- **AND** it performs no file access through the link
- **AND** `session-cwd` does not try the session directory

#### Scenario: Missing base returns correction

- **GIVEN** a tool context has no project or session directory
- **WHEN** a first-party filesystem tool receives a relative path
- **THEN** it returns `invalid_context`
- **AND** it performs no filesystem access

### Requirement: Failed filesystem operations do not change project context

A denied or failed `set_working_directory` or filesystem call SHALL NOT change the project directory or recent-file context. Only a validated successful `set_working_directory` receipt SHALL replace the project and reload its instructions. Another tool receipt SHALL NOT declare project scope.

Project-effect example:

```text
current project = /workspace/old

set_working_directory("/workspace/new")
  -> Success + DeclaredProjectDirectory("/workspace/new")
  -> project becomes /workspace/new
  -> project instructions reload

file_read("README.md")
  -> Success + Read("/workspace/new/README.md")
  -> RecentFiles changes
  -> project remains /workspace/new

hypothetical file_read receipt carrying DeclaredProjectDirectory("/outside")
  -> actor rejects the project effect
  -> project remains /workspace/new
```

#### Scenario: Denied declaration leaves prior project intact

- **GIVEN** a session declares `/workspace/old`
- **WHEN** `set_working_directory` is denied for `/workspace/new`
- **THEN** the project directory remains `/workspace/old`
- **AND** project instructions are not loaded from the denied path

#### Scenario: Unrelated receipt cannot replace project

- **GIVEN** a successful file tool receipt contains a project directory
- **WHEN** the actor applies the receipt
- **THEN** the actor rejects the project effect
- **AND** it does not reload project instructions
