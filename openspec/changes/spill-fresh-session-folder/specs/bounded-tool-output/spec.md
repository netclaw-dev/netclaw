## ADDED Requirements

### Requirement: Spill continuation does not depend on an earlier shell call

The dispatcher SHALL keep an oversized result and return a continuation call ID in each session that has a session directory path, also when that directory does not exist yet. The dispatcher SHALL create the session directory for the spill when it is missing. `tool_output_read` SHALL NOT create a directory.

When the dispatcher cannot keep the full result, the result SHALL say that Netclaw did not keep the full output and that `tool_output_read` cannot continue the call. The result SHALL NOT include a call ID for continuation, a path, or a direction to shell, grep, or `file_read`. The dispatcher SHALL write one warning record, `tool_output_spill_not_retained`, with the reason, the session ID, and the call ID.

The dispatcher SHALL NOT write a spill through a link at the session directory path.

Positive example:

```text
session directory = <session>/workspace   (does not exist; no shell call ran)
tool              = skill_load, result 56,948 characters, budget 12,000

model receives:
  <bounded head and tail>

  [output truncated to 12000 chars of 56948; continue with
   tool_output_read using CallId='call-example' and a bounded Start/Limit
   window instead of re-running]

tool_output_read(CallId='call-example', Start=12000, Limit=10000)
  returns characters 12000 to 21999 of the full result
```

Negative example:

```text
session directory path is a link (its target exists or does not exist)

model receives:
  <bounded head and tail>

  [output truncated to 12000 chars of 56948; Netclaw did not keep the full
   output, so tool_output_read cannot continue this call ...]

log has:
  tool_output_spill_not_retained reason=UnsafeSessionFolder ...

Netclaw writes no file behind the link.
```

Owner: the dispatcher owns this decision for each call. The spill file is durable session data. The warning is a log record.

#### Scenario: First oversized result in a session with no shell call

- **GIVEN** a session has a session directory path that does not exist
- **AND** no shell call ran in the session
- **WHEN** `skill_load`, `skill_read_resource`, or another tool returns a result over its inline budget
- **THEN** the result names `tool_output_read` with the call ID of that call
- **AND** `tool_output_read` with that call ID returns the text that the inline window removed

#### Scenario: A spill that Netclaw cannot keep is stated in the result

- **GIVEN** a result exceeds its inline budget
- **AND** Netclaw cannot write the spill (no session directory, an unusable call ID, a link at the session directory path, or a write error)
- **WHEN** the dispatcher bounds the result
- **THEN** the result says that Netclaw did not keep the full output
- **AND** the result has no continuation call ID and no path
- **AND** the log has one `tool_output_spill_not_retained` warning with the reason

#### Scenario: A link at the session directory path gets no spill

- **GIVEN** the session directory path is a link
- **WHEN** a result exceeds its inline budget
- **THEN** Netclaw creates no directory and no file behind the link
- **AND** the result says that Netclaw did not keep the full output

#### Scenario: The continuation tool follows the audience profile

- **GIVEN** a Public session whose profile does not allow `tool_output_read`
- **AND** a result of that session was spilled
- **WHEN** the model calls `tool_output_read` with the call ID
- **THEN** the dispatcher denies the call
