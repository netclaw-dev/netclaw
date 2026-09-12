## Why

Background jobs can wait after approval and then start without a current policy check. Three separate process start paths can drift.

## What Changes

- Use one checked start boundary for normal, streamed, and background shell calls.
- Preserve the exact command, shell, cwd, environment, and call authority across the queue.
- Recheck policy at launch. Keep each existing process owner responsible for output, timeout, cancellation, and disposal.
- **BREAKING:** Require a checked launch in the in-process background submission contract and execution actor constructors. Persisted job records retain their current format.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `canonical-shell-execution`: Require a shared checked start and current authority at launch.

## Impact

This change implements T12–T14 of the accepted shell authorization plan after PR #2117. It supports PRD-006 tool authority.
The scope includes the dispatcher, shell tool, session pipeline, and background actors.
It excludes new tools, grant formats, parser semantics, and deployment.

### Security and operational impact

A queued job can fail when its grant expires or its path becomes protected. Job status must report that failure.
Restart recovery continues to mark unfinished jobs as lost. It does not replay stored commands.
