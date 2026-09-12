## Why

Live use exposed path-authorization gaps, inconsistent receipt outcomes, and
tool choices that can flood model context. The merged contracts also differ
from current runtime behavior and from the smallest useful tool surface.

This change supports PRD-001 outcomes 7 and 9, PRD-006 outcome 5, and PRD-007 outcomes 3 and 5.

## What Changes

- Fix relative workspace access when a declared project has a symlink or junction ancestor.
- Define shared engineering terms and show the tool flow with pseudocode.
- Classify policy denials at the shared receipt boundary for parent and child sessions.
- Restrict project-scope receipt effects to `set_working_directory`.
- Use a closed remediation code and keep approval requests non-terminal.
- **BREAKING** Remove the unreleased `json_read` tool and its public type.
- **BREAKING** Remove the unreleased `file_read_many` tool and its public type.
- Replace their eval cases with composed `file_search` and `file_read` use.
- Define spilled-output continuation only through an opaque call id and `tool_output_read`.
- Define deferred-tool load as schema exposure, not execution authority.
- Tell an agent to load a known exact tool name without a prior search.
- Make the subagent replay create a real child actor and inspect its catalog.
- Rerun deterministic and hosted evals after the final tool surface exists.

### In scope

- Workspace path, receipt, and project-scope security boundaries.
- The shared engineering glossary and examples for these boundaries.
- Removal of two unreleased bulk tools.
- Core tool schemas, agent guidance, replay fixtures, and eval cases.
- Parent and child deferred-tool exposure contracts.
- Canonical specifications for spilled output.
- Concrete positive and negative examples for every changed authority, lifetime,
  state, and output boundary.

### Out of scope

- A new grep or search feature for spilled output.
- Executable-specific shell syntax or policy rules.
- A general tool-runtime rewrite.
- A new durable receipt or session format.
- A change to shell approval authority.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-tools`: Remove bulk read and JSON projection tools, and strengthen receipt ownership.
- `session-cwd`: Select an available relative base and do not retry after the
  `netclaw-tools` path access decision denies it.
- `bounded-tool-output`: Make the opaque continuation tool the only model-visible spill route.
- `netclaw-subagents`: Require a real child-catalog replay and consistent denial outcomes.
- `progressive-tool-disclosure`: Clarify schema exposure, exact-name loads, and dispatch authorization.

## Impact

### Code and APIs

The change affects workspace tools, path authorization, receipt classification,
tool registration, prompts, skills, tests, and evals. It removes two public tool
classes that no released tag contains. Public durable session and approval
formats stay unchanged.

### Security

The path fix closes an ancestor-link escape for relative file access. Receipt checks keep policy denials and project changes consistent across parent and child sessions. Every deferred call still passes normal authorization.

### Operations

The core tool catalog becomes smaller. Existing sessions can no longer call `json_read` or `file_read_many`. Agents use bounded, composable file search and file read calls instead.
