## Why

PRD-002 SEC-009 requires shell work to stay in a registered project or configured scratch directory. Post-0.26.0 live evidence shows headless subagents repeatedly creating disposable work under the shared platform temporary root because their execution scope contains `session_dir` but their model-visible working context omits it, producing avoidable parent approval prompts.

The existing `redirect-shared-temp-to-session-scratch` contract intended Personal and Team headless agents to receive this guidance, but its eval tells the parent agent which directory to use and does not exercise delegated work. This change closes that implementation and verification gap.

## What Changes

- Include the exact private `session_dir` and scratch-purpose guidance in Personal and Team subagent working context.
- Keep Public subagent context path-redacted and leave tool exposure unchanged.
- Preserve explicitly required platform-temporary paths; guidance does not rewrite calls, grant shell authority, or relax dynamic-command policy.
- Replace the tautological parent-only scratch eval with delegated disposable-work coverage that does not tell the child which directory to choose.
- Add deterministic prompt tests for Personal, Team, and Public subagent contexts.
- Keep automated session-directory cleanup out of scope.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-cwd`: Require Personal and Team subagent working context to announce the exact private session scratch directory while preserving Public path redaction.
- `tool-approval-gates`: Require delegated headless eval coverage that proves scratch guidance influences subagent path selection without conferring authority.

## Impact

- **Code:** Subagent initial working-context assembly and its prompt tests.
- **Evals:** Headless approval-alignment cases in `evals/run-evals.sh`.
- **Public APIs and persistence:** No change.
- **Dependencies:** No new package or service.
- **Security:** The exact path is disclosed only to Personal and Team subagents that already receive the same bound session directory in their execution authority. Public context remains redacted. No policy grant, safe root, or execution permission changes.
- **Operations:** Expected approval volume falls when subagents choose existing session scratch for disposable multi-command work. Retention and cleanup remain unchanged.
