## Context

`ShellTool` prefixes completed process output with the exit status.
The dispatcher keeps only the last 2,000 characters of a large shell result.
That tail removes the prefix on both execution paths.

Use the [engineering glossary](../../../../docs/spec/GLOSSARY.md) for shared terms.

## Goals / Non-Goals

Goals include tail-only inline output, visible exit status, deterministic tests, and accurate model guidance.
New result types, approval changes, and configuration changes are outside this fix.

## Decisions

`ShellTool` owns the authoritative process status as call-local data.
Reuse one result formatter for both completion paths.
Keep the current prefix and append the same status only when the result exceeds the existing shell inline budget.
This preserves small results and avoids a parser for result text.

The dispatcher retains its current order:

```text
execute the authorized tool
format the shell capture and process status
redact the result
keep the inline tail
write the session-scoped redacted spill
append the continuation notice
```

The dispatcher owns the inline budget and spill decision as call-local data.
The spill remains durable under the current session directory.
Actor boundaries and persistent record types do not change.

A new structured metadata contract would require more changes than this fix needs.
A larger inline budget would only move the failure threshold.

## Risks / Trade-offs

Large retained shell results contain the status at both ends.
The inline result contains the final status and needs no extra continuation call to determine the process outcome.
A failed spill still returns that status and the current explicit loss notice.
Command timeout and cancellation retain their current error results.
