## MODIFIED Requirements

### Requirement: Central bound + spill for every tool result

`DispatchingToolExecutor` SHALL bound every tool result to an inline budget and,
when the result exceeds that budget, return only its last budget characters plus
a separator and a continuation notice. This applies uniformly to every
tool, for the main session and for sub-agents (both run tools through the
dispatcher). The bound SHALL be applied after the dispatcher's central secret
redaction, so the inline result and any spilled file are redacted from one pass.
Individual tools SHALL NOT window, redact, or spill their results; they only bound
their own capture for memory safety and return the raw bounded result.

#### Scenario: Result under budget returned unchanged

- **WHEN** a tool returns a result at or below its inline budget
- **THEN** the dispatcher returns it unchanged
- **AND** no spill file is created

#### Scenario: Result over budget windowed to tail only

- **WHEN** a tool returns a result larger than its inline budget
- **THEN** the inline result contains only the last budget characters of the result
- **AND** a separator marks the discarded prefix
- **AND** the retained spill contains the prefix that the inline result omits

#### Scenario: Same bounding for sub-agent tool calls

- **WHEN** a sub-agent runs a tool that returns an oversized result
- **THEN** the same dispatcher bound + spill applies (sub-agents are not exempt)

### Requirement: Spill to a session-scoped file with a steer

When a result exceeds its inline budget, the dispatcher SHALL write the full redacted result to an internal file under the current session `tool-calls` directory. The dispatcher SHALL derive the file name from the sanitized call id. It SHALL return the opaque call id and a steer to `tool_output_read`. It SHALL NOT reveal the raw spill path or direct the model to shell, grep, or `file_read`. When no session directory or call id is available, the dispatcher SHALL return the inline window without a spill steer.

Concrete result shape:

```text
tool result size = 40,000 characters
inline budget    = 12,000 characters
call id          = call-example

model receives:
  <bounded tail>

  [output truncated to 12000 chars of 40000; continue with
   tool_output_read using CallId='call-example' and a bounded Start/Limit
   window instead of re-running]

internal storage may use:
  <session>/tool-calls/call-example.txt

model never receives:
  <session>/tool-calls/call-example.txt
  "run grep on the saved file"
  "use file_read on the saved file"
```

Counterexamples:

| Input state | Required behavior |
|---|---|
| Result fits the inline budget | Return it without a spill or continuation steer. |
| Result is oversized but the call id is absent | Return only the bounded inline window. Do not invent an id or reveal a path. |
| Call id is `../../outside` | Sanitize or reject the spill location; never write outside the session spill directory. |
| Spill write fails | Preserve the bounded inline result without claiming that continuation exists. |

#### Scenario: Spill file stays internal

- **WHEN** a result over budget is produced in a session with a directory
- **THEN** the full redacted result is written under the session `tool-calls` directory
- **AND** the inline result includes the opaque call id
- **AND** the steer names `tool_output_read`
- **AND** the steer contains no filesystem path

#### Scenario: Spilled file is redacted

- **WHEN** a result that contains a secret is spilled
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

## ADDED Requirements

### Requirement: Shell exit status survives inline truncation

`ShellTool` SHALL include the process exit status in every successful process-completion result.
For an oversized result, it SHALL place that status at the end of its result before the dispatcher applies the inline budget.
The dispatcher SHALL retain that status in the inline tail.
This rule applies to both execution paths and to zero and nonzero exit codes.
The status describes the process outcome, not approval or tool authorization.

#### Scenario: Oversized successful command retains its exit status

- **GIVEN** an authorized shell command produces more than 2,000 result characters
- **WHEN** the command exits with code zero through either execution path
- **THEN** the inline result contains `Exit code: 0` and the output tail
- **AND** the inline result omits the output prefix
- **AND** the retained spill contains the bounded capture and its exit status

#### Scenario: Oversized failed command retains its exit status

- **GIVEN** an authorized shell command produces more than 2,000 result characters
- **WHEN** the command exits with a nonzero code through either execution path
- **THEN** the inline result contains that exact exit code and the output tail
- **AND** the inline result does not report `Exit code: 0`
- **AND** the retained spill contains the same exit status
