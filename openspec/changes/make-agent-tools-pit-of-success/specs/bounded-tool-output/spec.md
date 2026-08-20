## ADDED Requirements

### Requirement: Spilled output has an opaque bounded continuation tool

The system SHALL provide a core `tool_output_read` tool that accepts an opaque
tool call id, start character, and character limit. It SHALL resolve only the
redacted spill belonging to that call id under the current immutable session
directory. It SHALL NOT accept a filesystem path, cross a session boundary, or
return more than the configured limit.

#### Scenario: Agent continues a current-session spill

- **GIVEN** a tool result was spilled under the current session for call id
  `call_example`
- **WHEN** `tool_output_read` requests a bounded later window for that id
- **THEN** the requested redacted window and continuation metadata are returned
- **AND** no shell or Python command is required

#### Scenario: Path-like call id cannot escape spill directory

- **GIVEN** a model supplies `../other-session/secret` as the call id
- **WHEN** `tool_output_read` validates the id
- **THEN** the outcome is `invalid_input`
- **AND** no path outside the current session spill directory is inspected

#### Scenario: Missing spill is recoverable without probing paths

- **GIVEN** no spill exists for the supplied current-session call id
- **WHEN** `tool_output_read` executes
- **THEN** the outcome is `not_found`
- **AND** the bounded result suggests rerunning or narrowing the originating
  structured tool
