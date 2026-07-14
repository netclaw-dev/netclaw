## Why

Netclaw's tool execution path has accumulated a 26-parameter pipeline entry point, nullable trust and capability fallbacks, and a mutable context that conflates run authority with per-call state. PR #1630 exposed the cost directly: parent context is flattened into more parameters and reconstructed inside subagents, increasing branching, coupling, and the risk that parallel calls or child agents share or silently lose security-relevant state.

This change is sourced from PRD-001 (FR-006 layered session context), PRD-002 (default-deny security envelope), PRD-006 (controlled and auditable tool execution), and PRD-007 (project/environment working context).

## What Changes

- **BREAKING** Remove context-free and nullable internal tool execution APIs, including `ToolExecutionContext.Empty`; every runtime invocation carries a required context.
- Introduce an immutable, value-object-based run scope and create a fresh mutable invocation context for every tool call.
- Replace the static long-parameter session pipeline with a composed pipeline service and one cohesive batch command.
- Require admitted `TurnContext`, audit, logging, and approval infrastructure for tool-enabled sessions instead of rebuilding authority from nullable `MessageSource` fallbacks.
- Prove which execution services are unconditional in the shipped runtime and make only those dependencies required; represent genuinely reachable absence explicitly without changing its behavior.
- Preserve existing background-routing and fallback semantics while removing null-driven branching where runtime construction proves the dependency is unconditional.
- Fork an independent child-run scope for each subagent and reconcile only a typed, successful child working-context delta into the parent.
- Separate asynchronous Git inspection from working-context snapshot composition and pure audience-aware rendering; Git inspection runs only for an eligible non-Public project directory that Git identifies as a repository.
- Preserve MCP as the external extension boundary and preserve its discovery, schema, grant, invocation, and result contracts.
- Deliver the work through three sequential, monitored PRs with durable OpenSpec/RALPH task tracking and post-merge verification.

In scope for MVP: internal API cleanup, value objects, required infrastructure, parallel-call isolation, subagent context isolation/reconciliation, Git-context separation, negative-path behavior, tests, evals, and operational guidance.

Out of scope: external in-process tool author compatibility, automatic subagent worktrees, durable subagent state, attribution of arbitrary shared-worktree changes, and refreshing Git state after every individual tool call.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-tools`: Require explicit run and invocation context, isolated per-call state, value-object boundaries, and complete auditing.
- `netclaw-subagents`: Fork child execution context once, maintain independent working state, and return an explicit successful delta.
- `tool-approval-gates`: Require admitted turn authority and approval infrastructure without nullable security fallbacks.
- `session-cwd`: Collect Git context asynchronously only for eligible repository-backed project directories.
- `audience-context-filtering`: Preserve Public suppression across asynchronous working-context capture.
- `actor-message-protocol`: Group local subagent execution context into a required framework-owned scope while preserving actor ownership.
- `netclaw-testing`: Add deterministic contract coverage for invocation isolation, child reconciliation, and stale Git-context continuations.

## Impact

- Affected assemblies: `Netclaw.Tools.Abstractions`, `Netclaw.Actors`, actor tests, daemon composition, eval fixtures, and system skills.
- Internal C# call sites and tests must migrate atomically within each staged PR; there is no compatibility shim because Netclaw ships as one executable and MCP is the extension boundary.
- Security improves by removing nullable authority/audit/approval dependencies from intended production paths without changing execution-mode behavior.
- Tool selection, dispatch mode, background behavior, authorization and approval outcomes, MCP payloads, persisted contracts, and model-visible results remain unchanged.
- Git remains a bounded local subprocess, but collection becomes asynchronous, cancellable, repository-gated, and stale-result-safe.
- No configuration schema or durable session-event migration is required.
