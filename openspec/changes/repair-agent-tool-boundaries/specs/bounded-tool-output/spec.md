## MODIFIED Requirements

The terms in this requirement use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: Spill to a session-scoped file with a steer

When a result exceeds its inline budget, the dispatcher SHALL write the full redacted result to an internal file under the current session `tool-calls` directory. The dispatcher SHALL derive the file name from the sanitized call id. It SHALL return the opaque call id and a steer to `tool_output_read`. It SHALL NOT reveal the raw spill path or direct the model to shell, grep, or `file_read`. When no session directory or call id is available, the dispatcher SHALL return the inline window without a spill steer.

Concrete result shape:

```text
tool result size = 40,000 characters
inline budget    = 12,000 characters
call id          = call-example

model receives:
  <bounded head and tail>

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
