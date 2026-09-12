# bounded-tool-output Specification

## Purpose

Define how Netclaw bounds tool output in memory, windows oversized results for
inline delivery, spills full output to a session-scoped file, and steers the
model toward targeted reads rather than re-running tools.

## Requirements

### Requirement: Central bound + spill for every tool result

`DispatchingToolExecutor` SHALL bound every tool result to an inline budget and,
when the result exceeds that budget, return a head+tail window of the budget plus
a steer pointing the model at the full output. This applies uniformly to every
tool, for the main session and for sub-agents (both run tools through the
dispatcher). The bound SHALL be applied after the dispatcher's central secret
redaction, so the inline result and any spilled file are redacted from one pass.
Individual tools SHALL NOT window, redact, or spill their results; they only bound
their own capture for memory safety and return the raw bounded result.

#### Scenario: Result under budget returned unchanged

- **WHEN** a tool returns a result at or below its inline budget
- **THEN** the dispatcher returns it unchanged
- **AND** no spill file is created

#### Scenario: Result over budget windowed to head and tail

- **WHEN** a tool returns a result larger than its inline budget
- **THEN** the inline result contains the head and the tail of the result within
  the budget, with the discarded middle marked

#### Scenario: Same bounding for sub-agent tool calls

- **WHEN** a sub-agent runs a tool that returns an oversized result
- **THEN** the same dispatcher bound + spill applies (sub-agents are not exempt)

### Requirement: Per-tool inline budget

The inline budget SHALL be a property of the tool. `INetclawTool.InlineOutputBudgetChars`
SHALL default to `0`, meaning "use the session content budget"
(`SessionTuning.MaxInlineToolResultChars`). A tool MAY override it: verbose tools
(e.g. `shell_execute`) override to a small value because their output is skimmed;
content tools keep the larger content budget because the model fetched them to
read in full. The dispatcher SHALL resolve the budget as the tool's override when
positive, else the session content budget.

#### Scenario: Verbose tool bounded small

- **GIVEN** a tool declares a small `InlineOutputBudgetChars` (e.g. shell)
- **WHEN** its output exceeds that small budget
- **THEN** the dispatcher windows it to the small budget and spills the rest

#### Scenario: Content tool bounded by the session content budget

- **GIVEN** a tool with no override (e.g. file_read, web_fetch, MCP)
- **WHEN** its output is at or below the content budget
- **THEN** the full result is returned inline with no spill

### Requirement: Spill to a session-scoped file with a steer

When a result exceeds its inline budget, the dispatcher SHALL write the full
redacted result to an internal file under the current session `tool-calls`
directory. It SHALL derive the file name from the sanitized call id and return
the opaque call id with a steer to `tool_output_read`. It SHALL NOT reveal the
raw spill path or direct the model to shell, grep, or `file_read`. When no
session directory or call id is available, the dispatcher SHALL return the
inline window without a spill steer.

#### Scenario: Spill file stays internal

- **WHEN** a result over budget is produced in a session with a directory
- **THEN** the full redacted result is written under the session `tool-calls` directory
- **AND** the inline result includes the opaque call id
- **AND** the steer names `tool_output_read`
- **AND** the steer contains no filesystem path

#### Scenario: Spilled file is redacted

- **WHEN** a result containing a secret is spilled
- **THEN** the internal spill file has the secret redacted
- **AND** redaction occurs before the spill write

#### Scenario: Call id cannot escape the spill directory

- **WHEN** the call id contains path-traversal characters
- **THEN** the spill file stays inside the tool-calls directory
- **AND** the dispatcher reveals no raw path

#### Scenario: Missing spill identity has no false continuation

- **GIVEN** a result exceeds its inline budget
- **AND** the invocation has no usable session directory or call id
- **WHEN** the dispatcher bounds the result
- **THEN** the model receives the bounded inline window
- **AND** the result does not claim that `tool_output_read` can continue it

### Requirement: Bounded-memory capture

Tools SHALL bound their own capture so peak managed memory is on the order of the
capture ceiling (`ToolConfig.MaxOutputChars`), independent of total output size.
No tool SHALL materialize the entire output of an arbitrarily large source as a
single in-memory string before bounding it.

#### Scenario: Large source does not scale memory

- **WHEN** a tool reads from a source far larger than the capture ceiling
- **THEN** peak managed allocation stays on the order of the capture ceiling
- **AND** the process is not OOM-killed by the capture

### Requirement: Spilled output has an opaque bounded continuation tool

The system SHALL provide a core `tool_output_read` tool that accepts an opaque
tool call id, start character, and character limit. It SHALL resolve only the
redacted spill that belongs to that call id under the current immutable session
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
- **AND** the bounded result suggests a narrower source call
