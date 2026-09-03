## ADDED Requirements

The terms in this specification use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: MCP tool-declared errors have a non-success receipt

An MCP result with the protocol field `isError: true` SHALL produce a
`transient_failure` receipt. Netclaw SHALL use the typed protocol field and
SHALL NOT infer failure from the result text.

The model-facing result SHALL keep the bounded formatted text. The receipt SHALL
record no successful file activity and grant no authority.

#### Scenario: Tool-declared failure is not successful

- **GIVEN** an MCP server returns HTTP success with `isError: true`
- **WHEN** Netclaw formats the tool result
- **THEN** the receipt category is `transient_failure`
- **AND** the model receives the bounded tool-declared detail

#### Scenario: Error text does not override a success receipt

- **GIVEN** an MCP server returns `isError: false` with text that starts with `Error:`
- **WHEN** Netclaw formats the tool result
- **THEN** the receipt category remains `success`

### Requirement: A cycle correction uses the normal result boundary

A blocked cycle batch SHALL produce one `recoverable_correction` receipt for
each requested call. Each receipt SHALL use a bounded `break_tool_cycle`
remediation code.

The correction SHALL pass through the normal model-visible tool result path.
It SHALL NOT invoke the requested tool or change authorization state.

#### Scenario: Blocked batch preserves call-result pairing

- **GIVEN** a cycle guard blocks a batch with three calls
- **WHEN** Netclaw creates correction results
- **THEN** each call identifier has exactly one matching tool result
- **AND** each receipt uses `recoverable_correction` and `break_tool_cycle`

#### Scenario: Correction grants no authority

- **GIVEN** a blocked call would require approval under current policy
- **WHEN** Netclaw returns the cycle correction
- **THEN** Netclaw does not request approval or execute the tool
- **AND** a later different call still uses the normal policy gates
