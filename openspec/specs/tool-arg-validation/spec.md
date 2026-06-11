# tool-arg-validation Specification

## Purpose

Defines the validation contract for LLM-supplied tool arguments at the
dispatch seam. No argument the model expressed intent through is ever silently
discarded, coerced, or overridden: unknown keys and invalid values reject the
call with a recoverable, self-describing error before execution, and every
honored-but-overridden value is surfaced in the tool result. Originated from a
production incident where a near-miss timeout key (`TimeoutSeconds` instead of
`_timeout_seconds`) was silently dropped, the shell timeout silently fell back
to a default, and the agent's false belief fed a stuck loop.

## Requirements

### Requirement: Unknown argument keys reject the call before execution

For native (first-party) tools, the dispatcher SHALL validate every supplied
argument key against the tool's recognized-key set before execution. A supplied
key is recognized if and only if it would be consumed downstream:

- a declared tool parameter, matched exactly or by deterministic key
  normalization (case/punctuation folding, mirroring existing flexible binding);
- a meta key (`_rationale`, `_timeout_seconds`, `_background`), matched
  **exactly only**.

A call carrying one or more unrecognized keys SHALL be rejected with a
tool-result error and the tool SHALL NOT execute. The error SHALL name each
unrecognized key, state that the tool was not executed, and list the tool's
valid argument names. MCP tools are exempt (server-side schema validation is
authoritative).

#### Scenario: Near-miss meta key rejected with suggestion

- **GIVEN** a `shell_execute` call with `"TimeoutSeconds": "1200"`
- **WHEN** the dispatcher validates argument keys
- **THEN** the call is rejected without executing the command
- **AND** the tool result contains `Unrecognized argument 'TimeoutSeconds'`,
  a `did you mean '_timeout_seconds'` suggestion, and the valid argument names

#### Scenario: Case-variant declared parameter still accepted

- **GIVEN** a `shell_execute` call with `"command": "ls"` (lowercase)
- **WHEN** the dispatcher validates argument keys
- **THEN** the key is recognized via deterministic normalization
- **AND** the tool executes exactly as it does today

#### Scenario: Exact meta key accepted

- **GIVEN** a tool call with `"_timeout_seconds": 300`
- **WHEN** the dispatcher validates argument keys
- **THEN** the key is recognized and extraction consumes it

#### Scenario: Wholly unknown key rejected without suggestion

- **GIVEN** a `file_read` call with `"Banana": true` and a valid `Path`
- **WHEN** the dispatcher validates argument keys
- **THEN** the call is rejected naming `Banana` with no near-miss suggestion
- **AND** the valid argument names for `file_read` are listed

#### Scenario: MCP tool exempt from native key validation

- **GIVEN** a tool call targeting an MCP server tool with an extra key
- **WHEN** the dispatcher processes the call
- **THEN** native key validation is skipped
- **AND** the MCP server's own schema validation result is returned observably

### Requirement: Fuzzy matching generates suggestions only — never acceptance

Near-miss matching SHALL be used solely to generate "did you mean" suggestion
text inside rejection errors (normalization equivalence against meta keys,
edit distance against recognized names). The system SHALL NOT bind, alias, or
otherwise act on a guessed key. Ambiguity SHALL always be resolved by the LLM
re-issuing the call explicitly.

#### Scenario: Near-miss key is never silently bound

- **GIVEN** a tool call with `"timeout_seconds": 300` (missing the `_` prefix)
- **WHEN** the dispatcher validates argument keys
- **THEN** the call is rejected with a `did you mean '_timeout_seconds'`
  suggestion
- **AND** no timeout override is applied from the near-miss key

### Requirement: Present-but-invalid argument values reject the call

For native tools, the system SHALL reject a call whose argument key is
recognized but whose value cannot be parsed as the declared type, with a
tool-result error naming the parameter, the supplied value, and the expected
type. The tool SHALL NOT execute. An absent optional parameter SHALL continue
to use its documented default (absence expresses no intent; invalidity does).
Numeric coercion SHALL NOT silently truncate: a non-integral value supplied
for an integer parameter is invalid.

#### Scenario: Unparseable integer rejects instead of coercing to zero

- **GIVEN** a `file_read` call with `"Limit": "abc"`
- **WHEN** arguments are bound
- **THEN** the call is rejected with an error naming `Limit`, the value
  `"abc"`, and the expected type integer
- **AND** the file is not read

#### Scenario: Non-integral number for integer parameter is invalid

- **GIVEN** a tool call supplying `12.7` for an integer parameter
- **WHEN** arguments are bound
- **THEN** the call is rejected (no silent truncation to 12)

#### Scenario: Absent optional parameter keeps its default

- **GIVEN** a `file_read` call that omits `Limit`
- **WHEN** arguments are bound
- **THEN** the documented default applies and no error is raised

### Requirement: Malformed tool-call arguments JSON rejects before dispatch

The pipeline SHALL produce a tool-result error for a tool call whose arguments
JSON the provider boundary fails to deserialize, stating the arguments were
not valid JSON; the tool SHALL NOT be dispatched with null or empty arguments.
The error SHALL include the parse failure detail so the model can correct its
emission.

#### Scenario: Truncated arguments JSON surfaces as an error result

- **GIVEN** a streamed tool call whose accumulated arguments JSON is truncated
  and fails to parse
- **WHEN** the pipeline processes the call
- **THEN** a tool-result error for that call id states the arguments were not
  valid JSON and the tool was not executed
- **AND** no tool receives a null-argument invocation

### Requirement: Overridden argument values are surfaced to the model

The system SHALL append a model-facing notice to a call's tool result whenever
it honors the call but applies a value different from the one the LLM
requested (clamping, flooring, capping), describing the requested value, the
applied value, and the reason. Notices SHALL be appended after output bounding
so they cannot be truncated away. Log-only signaling SHALL NOT satisfy this
requirement: the notice MUST appear in the tool result the model reads.

#### Scenario: Notice survives output bounding

- **GIVEN** a tool call whose result exceeds the inline output budget
- **AND** an override notice applies to the call
- **WHEN** the result is bounded and spilled
- **THEN** the notice is present in the inline result returned to the model
