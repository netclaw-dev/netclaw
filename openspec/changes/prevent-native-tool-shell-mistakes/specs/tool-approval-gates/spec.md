## ADDED Requirements

### Requirement: Exact native-tool shell executables receive an agent correction

After shell input validation, audience checks, protected-path checks, and complete static analysis succeed, but before stored-grant matching, approval, or execution, the system SHALL inspect parser-owned command occurrences. If the first authored executable token of an occurrence exactly names an audience-visible first-party Netclaw tool other than `shell_execute`, the system SHALL stop the entire shell call and return a typed native-tool correction. It SHALL use ordinal exact name comparison and SHALL NOT infer private native-tool syntax from shell arguments.

#### Scenario: Bare deferred tool name is corrected and exposed

- **GIVEN** `list_reminders` is a policy-visible deferred first-party tool
- **WHEN** the agent invokes `shell_execute` with the static command `list_reminders`
- **THEN** no shell process starts
- **AND** no approval request is emitted
- **AND** the result tells the agent to call the named native tool directly
- **AND** the next model request contains the `list_reminders` schema

#### Scenario: Static compound call is stopped as one unit

- **GIVEN** a complete static shell command contains an occurrence whose exact executable token is a policy-visible first-party tool
- **AND** the command also contains arguments, redirects, a pipeline stage, or another compound occurrence
- **WHEN** the shell call reaches authorization
- **THEN** the complete shell call is not executed
- **AND** the system selects the first matching occurrence as the correction target
- **AND** it does not interpret any shell argument as a native-tool argument

#### Scenario: Dynamic and unknown identities remain shell work

- **GIVEN** a shell executable identity is dynamic, unresolved, unknown, fuzzy, aliased, or path-qualified rather than an exact authored first-party tool name
- **WHEN** the shell call reaches authorization
- **THEN** the native-tool correction does not apply
- **AND** the existing shell policy determines deny, approval, or execution

#### Scenario: Hard deny takes precedence

- **GIVEN** a shell call contains an exact visible native-tool executable token
- **AND** ordinary preflight detects invalid input, a protected path, or another terminal deny
- **WHEN** authorization runs
- **THEN** the terminal denial is returned
- **AND** no tool schema is activated

#### Scenario: Hidden, MCP, and shell targets are excluded

- **GIVEN** the exact executable token names a hidden or denied first-party tool, an MCP tool, or `shell_execute`
- **WHEN** the shell call reaches authorization
- **THEN** no native-tool correction confirms or activates that target
- **AND** the existing shell path remains authoritative

#### Scenario: Eventual native call keeps normal authority

- **GIVEN** the model receives a native-tool correction and the target schema
- **WHEN** it invokes that native tool on a later model iteration
- **THEN** normal argument validation, audience policy, approval, and dispatch run
- **AND** the correction creates no one-time, session, folder, or global grant
