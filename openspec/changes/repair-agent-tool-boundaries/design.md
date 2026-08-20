## Context

The prior tool work added relative paths, call-local receipts, progressive disclosure, and several focused tools. Live use and adversarial review exposed three problems.

First, a relative project base can contain an unsafe ancestor link. Second, receipt categories differ between parent and child policy denials. Third, two bulk tools duplicate smaller tools and can consume too much model context.

The canonical spill contract also still describes a raw file path and shell grep. The runtime now exposes an opaque `tool_output_read` route instead.

Constraints:

- ACL, audience, approval, path, and MCP policy remain authoritative.
- Public durable session and approval formats do not change.
- No released tag contains `json_read` or `file_read_many`.
- ShellSyntaxTree remains the owner of shell syntax facts.
- Netclaw adds no executable-specific command parser.

## Goals / Non-Goals

**Goals:**

- Close the ancestor-link path escape.
- Produce one receipt category for the same failure in every actor path.
- Remove two bulk tools before their first release.
- Keep the agent tool surface small and composable.
- Align prompts, skills, fixtures, and canonical specifications.
- Test a real child catalog instead of a parent substitute.

**Non-Goals:**

- Add search or grep to spilled output.
- Block a known deferred tool name at dispatch before schema load.
- Change shell approval authority.
- Add a durable receipt format.
- Preserve compatibility for unreleased tool classes.

## Decisions

### 1. The path policy validates the complete selected base chain

`ScopedFileAccessPolicy` will select one relative base. It will validate that base against its owning allowed root before it resolves the authored path.

The validation will reject a link or junction in the base or its ancestors below the owning root. It will preserve the current root, audience, and protected-path checks.

The policy will not retry against session scratch after a project-based denial. A stale project can use the existing session fallback only before path authorization starts.

Alternative: inspect only the final base. This leaves an ancestor-link escape.

### 2. The dispatcher owns terminal receipt classification

The dispatcher will include authorization in its receipt classification boundary. A `ToolAccessDeniedException` will produce `access_denied` for all callers.

A `ToolApprovalRequiredException` will stay non-terminal. The caller can still complete an approval and retry the same call.

Workspace tools can set a more exact terminal receipt. The dispatcher will fill a receipt only when the tool has not set one.

Alternative: keep actor-specific exception maps. That design already caused parent and child drift.

### 3. Only one named tool can declare project scope

Actors will apply `DeclaredProjectDirectory` only for a successful `set_working_directory` receipt. Another tool cannot mutate project scope through the internal receipt seam.

Remediation codes will use a bounded internal value set. They will reject controls and excessive length.

Alternative: trust any internal receipt producer. This makes future tool additions able to widen scope by accident.

### 4. Remove bulk readers and keep composable primitives

The implementation will remove `json_read` and `file_read_many`. It will remove their registration, audience entries, schemas, guidance, tests, and eval cases.

An agent can compose these retained tools:

- `file_search` locates candidate files.
- `file_read` reads one bounded file window.
- Parallel tool calls read several known files without one bulk result.
- `tool_output_read` continues one spilled result by opaque call id.

The replay corpus will not preserve a product expectation for a removed tool. A retained scenario can assert the composed route when that route matches the original intent.

Alternative: keep the bulk tools with lower bounds. The larger schemas and batch results still duplicate simpler calls and invite context floods.

### 5. Loading controls schema exposure only

`load_tool` adds a deferred schema to one actor's model-visible set. It does not grant execution authority.

Dispatch will still resolve a known registered name and run normal authorization. This preserves compatibility with models that recall a name from earlier context.

Guidance will tell an agent to call `load_tool` directly when it knows the exact name. The agent will use `search_tools` only when it knows an intent but not a name.

Alternative: require an activation lease before dispatch. This adds a second authority-like state and does not improve the real approval boundary.

### 6. Spill continuation uses one opaque contract

The dispatcher will not reveal a raw spill path to the model. Its steer will name the call id and `tool_output_read`.

The canonical specification will remove the old `file_read` and shell grep route. A later design can assess search over spilled output.

Alternative: expose the spill file to file tools or shell. This couples continuation to filesystem authority and lower-audience shell access.

### 7. Tests follow actual actor and policy boundaries

The security PR will add POSIX and native Windows ancestor-link tests. It will also add executor, parent, and child policy-denial receipt tests.

The tool-removal PR will update the core snapshot and remove bulk-tool fixtures. The rollout PR will create a real subagent for the child catalog replay.

The final eval run will use the reduced surface. Public evidence will contain aggregate, PII-free results only.

## Actor Boundaries and Persistence

`DispatchingToolExecutor` owns authorization and terminal receipt classification. `LlmSessionActor` and `SubAgentActor` consume the same receipt facts.

Only those actors update their current work context. The main session persists its existing `WorkingContext`. A child keeps its context ephemeral.

This change adds no event, snapshot, protobuf, or configuration field. Recovery behavior stays unchanged.

## Failure Modes and Recovery

- Unsafe project ancestor: return `access_denied`; do not access the file.
- Policy denial: return or propagate the existing bounded denial with an `access_denied` receipt.
- Approval request: record no terminal receipt; retry after the approval result.
- Removed tool name: return the normal hidden or unknown tool result.
- Missing spill: return `not_found` for the opaque call id.
- Failed project declaration: keep the previous project and instructions.
- Eval provider failure: mark infrastructure failure; do not count it as task behavior.

## Risks / Trade-offs

- [Agents may need more file calls] -> Keep parallel `file_read` calls and strict output bounds.
- [A recalled deferred name can bypass schema load] -> Keep normal authorization and define load as exposure only.
- [Native link tests need platform support] -> Use deterministic platform tests and explicit skips only when link creation is unavailable.
- [Corpus changes can erase historical intent] -> Keep sanitized intent and source classification, but remove product expectations for deleted tools.
- [The stack can drift after a rebase] -> Rebase each branch and compare patch identity before PR publication.

## Migration Plan

1. Merge the path and receipt security fixes.
2. Remove both unreleased bulk tools and update the tool surface.
3. Repair the spill, discovery, replay, and eval contracts.
4. Run deterministic tests on each PR.
5. Run hosted evals once on the final stacked head.

Rollback uses normal code reversal. No persisted state requires migration.

## Open Questions

The design for search inside spilled output remains open. It requires a separate security and product review.
