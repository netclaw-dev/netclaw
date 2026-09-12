## Why

Agents currently choose shared operating-system temporary directories for disposable shell artifacts, which places otherwise routine diagnostic work outside Netclaw's per-session safe space and produces avoidable approval prompts. PRD-002 SEC-009 and PRD-007 already define shell work as project- or scratch-scoped, so Netclaw should steer this intent to the existing private session directory before it reaches the user approval surface.

## What Changes

- Detect shell calls whose explicit working directory is the platform's shared temporary root and whose ordinary temporary-artifact intent can be satisfied inside the existing session directory.
- Return a typed Personal agent-facing correction before user approval that identifies `{session_dir}` as the private scratch directory and asks the agent to retry an explicitly authored call there. Preserve the existing Team and Public shell denial boundary.
- Preserve the original tool call and result in model history; Netclaw does not rewrite the command, working directory, or arguments.
- Keep exact one-time approval available when the agent intentionally retains the shared temporary directory.
- Apply the same correction path to parent agents and subagents before either reaches a user or parent approval bridge.
- Keep headless and other noninteractive runs free of correction deferrals; use model guidance to prefer session scratch without prohibiting explicitly required platform-temp work.
- Keep Public path redaction plus hard deny, protected-path, dynamic-command, unsafe-command, and non-shell behavior unchanged.
- Treat automated retention and cleanup of session directories as out of scope for this change. A later change can define age, ownership, observability, and failure semantics for cleanup.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-cwd`: Define the existing per-session directory as the model-visible private scratch alternative to shared operating-system temporary roots.
- `tool-approval-gates`: Define the bounded pre-approval correction, exact-call preservation, one-time approval fallback, and parent/subagent parity.

## Impact

- **Code:** Session tool-execution pipelines, subagent approval bridging, shared shell authorization results, and working-context guidance.
- **Public APIs:** No public API or persisted approval-store format changes; platform-temp capture remains an internal policy service rather than a new `ShellExecutionEnvironment` member.
- **Dependencies:** No new package or operating-system dependency.
- **Security:** Shared temporary roots do not become trusted roots, and the correction grants no authority. Existing hard-deny and protected-path checks retain precedence. The session directory remains bounded to the current session's configured safe space.
- **Operations:** Approval volume should fall for agents that accept the correction. Existing session-directory storage behavior is unchanged; this change adds no purge, retention, or background cleanup process.
