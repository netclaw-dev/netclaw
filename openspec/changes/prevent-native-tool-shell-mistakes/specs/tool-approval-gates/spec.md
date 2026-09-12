## ADDED Requirements

The terms in this requirement use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: Exact native-tool shell executables receive an agent correction

After shell input and audience validation, complete ShellSyntaxTree analysis, and all terminal preflight checks succeed, but before stored-grant matching, approval, or execution, the system SHALL inspect parser-owned command occurrences.

If the first authored executable token of an occurrence exactly names a
policy-visible first-party Netclaw tool other than `shell_execute`, the system
SHALL stop the entire shell call. It SHALL return the closed `UseNativeTool`
remediation code with a `NativeToolSuggested` correction fact.

The comparison SHALL be ordinal and exact. The system SHALL NOT infer private
native-tool syntax from shell arguments.

Boundary examples:

| Authored executable form | Required result |
|---|---|
| `list_reminders` | Return the correction and stop the shell call. |
| `list_reminders --all` | Return the correction. Do not translate `--all`. |
| `./list_reminders` | Keep the normal shell path. The token is path-qualified. |
| `$tool_name` | Keep the normal shell path. The identity is dynamic. |
| `list-reminder` | Keep the normal shell path. The name is not exact. |

Exact correction example:

```text
shell_execute(Command = "list_reminders")

result:
  Shell execution stopped because 'list_reminders' is a native Netclaw tool.
  Next action: call the native Netclaw tool named in this result directly instead of shell_execute.

receipt:
  category    = RecoverableCorrection
  remediation = UseNativeTool

authorization correction fact:
  NativeToolSuggested("list_reminders")

call-local exposure request:
  ToolExposureRequest("list_reminders")
```

Source-order counterexamples:

| Authored shell input | Selected target |
|---|---|
| `file_write --path first && file_read --path second` | Select `file_write`. Execute no part of the shell call. |
| `sudo bash -lc "file_read"; file_write` | Select `file_read`. Preserve the wrapper payload before the later outer command. |
| `echo ready; file_read --path report.txt` | Select `file_read`. Do not execute the earlier `echo`. |

Precedence:

```text
input and audience checks
  -> complete ShellSyntaxTree analysis
  -> protected-path, hard-deny, and other terminal preflight checks
  -> exact native-tool detector
  -> stored grants, approval, or shell execution
```

A terminal preflight result stops the flow before the detector. A detector
match stops the flow before grants, approval, or execution.

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

- **GIVEN** a shell call contains an exact policy-visible native-tool executable token
- **AND** ordinary preflight detects invalid input, a protected path, or another terminal deny
- **WHEN** authorization runs
- **THEN** the terminal denial is returned
- **AND** no tool schema is activated

#### Scenario: Hidden, MCP, and shell targets are excluded

- **GIVEN** the exact executable token names a hidden or denied first-party tool, an MCP tool, or `shell_execute`
- **WHEN** the shell call reaches authorization
- **THEN** no native-tool correction confirms or activates that target
- **AND** the existing shell path remains authoritative

#### Scenario: Child-static-denied target is not disclosed

- **GIVEN** a subagent authors `attach_file` or `spawn_agent` as an exact shell executable
- **AND** child policy omits that registration from the child-private registry
- **WHEN** the child shell call reaches authorization
- **THEN** no native-tool correction confirms the denied name
- **AND** no schema exposure request is created
- **AND** the existing shell path remains authoritative

#### Scenario: Eventual native call keeps normal authority

- **GIVEN** the model receives a native-tool correction and the target schema
- **WHEN** it invokes that native tool on a later model iteration
- **THEN** normal argument validation, audience policy, approval, and dispatch run
- **AND** the correction creates no one-time, session, folder, or global grant
