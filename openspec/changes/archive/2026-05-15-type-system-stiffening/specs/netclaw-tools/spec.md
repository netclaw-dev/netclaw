## ADDED Requirements

### Requirement: Tool execution context carries a parsed audience

`ToolExecutionContext` SHALL represent the execution audience as a parsed
`TrustAudience`, not as an unvalidated wire string. The audience SHALL be
parsed when the context is built, so an unparseable value fails at construction
rather than at a later tool authorization check. Tool authorization SHALL read
the parsed audience directly and SHALL NOT re-parse a string or apply a
parse-failure fallback to `Public`.

#### Scenario: Context built with an unparseable audience fails loud

- **WHEN** a `ToolExecutionContext` is built from an audience value that cannot
  be parsed
- **THEN** construction throws an explicit parse error
- **AND** the failure occurs before any tool runs

#### Scenario: Tool authorization reads the parsed audience

- **GIVEN** a `ToolExecutionContext` carrying a parsed `TrustAudience`
- **WHEN** `ToolAccessPolicy` evaluates a tool invocation
- **THEN** it reads the audience as a typed value
- **AND** it performs no string parsing and applies no `Public` parse-failure
  fallback
