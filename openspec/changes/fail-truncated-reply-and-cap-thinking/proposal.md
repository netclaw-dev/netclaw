## Why

A model reply that ends with finish reason `length` and has no tool call is cut
off. Netclaw treated it as a normal text answer. A sub-agent returned the cut
text as success, and a session posted it. The parent and the user could not
tell that text was missing.

A model call that streams only thinking never gave an answer, but each thinking
update reset the no-progress deadline. The call could run without a bound.

Source PRDs: `PRD-001`, `PRD-006` (through `docs/spec/SPEC-016-tool-liveness-and-stall-detection.md`).

## What Changes

- A reply with finish reason `length` and no tool call fails. It is never a
  success.
  - A sub-agent fails with reason `output_truncated`. It makes no retry. Its
    output holds a redacted preview of at most 300 characters. The cut falls on
    a text-element boundary.
  - A session ends the turn with an error that says the reply was truncated. It
    does not post the cut-off text. The error category is `ModelBehavior`
    (`category=model` in headless output).
  - A `length` reply that has a tool call still runs its tools.
  - A thinking-only or empty `length` reply still follows the existing retry
    path.
- A model call that streams only thinking stops at three times the no-progress
  timeout.
  - Thinking resets the no-progress deadline but not the cap.
  - Only an answer update restarts the cap. An answer update is non-empty text,
    a tool call, or a finish reason. Empty or whitespace-only text counts as
    thinking.
  - A session ends the turn with an error that names thinking. The error
    category is `ModelBehavior`, not `Timeout`. A sub-agent fails with reason
    `thinking_only_timeout`.
- No new configuration key. The cap derives from the existing no-progress
  timeout (`SessionConfig` and `SubAgentConfig.NoProgressTimeoutSeconds`).

Out of scope: the compaction summary and the session memory observer still
accept a `length` reply. A follow-up item tracks them.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-session`: two added requirements. A truncated reply fails the turn,
  and a thinking-only model call is capped. Both errors have category
  `ModelBehavior`.
- `netclaw-subagents`: two added requirements. A truncated reply fails the run
  with reason `output_truncated`, and a thinking-only model call fails the run
  with reason `thinking_only_timeout`.

The deltas add requirements only. They do not copy an existing requirement, so
a later sync does not overwrite text that other changes own.

## Impact

- Code (already in this branch): `StreamProgress.Classify`, `ProcessingWatchdog`,
  `LlmSessionActor`, `SubAgentActor`, `StreamPingGate`, `SubAgentRunMetadata`.
- Docs (already in this branch): `docs/runbooks/subagents.md`, SPEC-016.
- Security: the preview passes through the redactor before the cut, so a secret
  that spans the cut point does not leak as a partial value. The change adds no
  grant, no tool, and no authorization context.
- Operations: a sub-agent run can now end with two new failure reasons. A
  provider that returns `length` for a long answer now fails the turn instead of
  posting a partial answer. The operator must raise the model output limit or
  shrink the request.
