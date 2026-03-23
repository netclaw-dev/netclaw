## Context

Netclaw already models three shell modes in configuration and trust-context policy: `off`, `sandbox-only`, and `host-allowed`. Today only `off` and `host-allowed` are executable. `sandbox-only` is intentionally fail-closed and currently returns a denial because there is no isolated runner behind it.

That gap is now visible in three places:

- the security contract says Netclaw should reserve a safer shell path for higher-risk contexts
- the runtime can distinguish `sandbox-only`, but it cannot execute useful work through it
- diagnostics cannot tell an operator whether a safer shell posture is genuinely ready or only nominally configured

This change is cross-cutting because it touches configuration, tool authorization, the shell tool runtime, and CLI diagnostics. The design must preserve existing actor boundaries: session actors continue to request tool execution through the current tool executor path, while sandbox lifecycle and host integration stay behind a dedicated runtime boundary. Persistence impact should stay minimal because sandbox execution is ephemeral and should not introduce new durable actor state beyond existing tool audit/session output.

## Goals / Non-Goals

**Goals:**
- Make `sandbox-only` a real executable shell mode with no host fallback.
- Introduce a daemon-owned sandbox runner abstraction so shell isolation mechanics do not leak into session actors or LLM-facing tool code.
- Define a per-invocation isolated workspace model that preserves useful project-relative execution while constraining writable state and captured artifacts.
- Add fail-closed validation and operator diagnostics for sandbox backend availability and misconfiguration.
- Keep `host-allowed` behavior stable for existing owner-operated personal deployments.

**Non-Goals:**
- No redesign of trust-context or audience policy beyond consuming the already-derived shell mode.
- No general sandbox framework for every tool in MVP; this change covers shell execution only.
- No persistence of sandbox filesystem state across invocations beyond explicit output artifacts returned through the existing session directory flow.
- No transparent fallback from sandbox to host execution.
- No requirement to support multiple runtimes in v1; the design should allow future expansion, but the first implementation may target a single container-backed runner.

## Decisions

### Decision: Shell execution branches through a dedicated sandbox runner interface

Add a dedicated runtime service boundary such as `IShellSandboxRunner` that accepts a normalized shell execution request and returns a structured result (exit code, stdout/stderr, timing, failure category, optional diagnostics metadata).

`ShellTool` and `DispatchingToolExecutor` remain responsible for policy gating, output shaping, and LLM-facing errors. They do not know whether isolation is provided by Docker, Podman, or another backend. The concrete runner is selected from configuration and injected by the daemon.

Rationale: this keeps session actors and tool definitions transport-agnostic, matches the existing dependency-injected tool runtime style, and lets diagnostics query the same runner for health/capability checks.

Alternative considered: invoke container runtime commands directly inside `ShellTool`. Rejected because it would mix policy, host integration, and user-visible error handling in one place.

### Decision: v1 sandbox backend is container-backed and daemon-owned

The first executable backend uses an isolated container invocation launched by the daemon host. The runtime contract is backend-neutral, but the implementation can target one container runtime in MVP, most likely Docker because the repo already uses Docker-based smoke infrastructure.

The backend config needs enough data to validate and launch safely:

- runtime command or backend kind
- sandbox image reference
- optional workspace root/scratch root on host
- default network mode
- resource/time limits that complement existing shell timeouts

Rationale: container execution is the smallest practical implementation path that matches the repo's current tooling and avoids inventing a new sandbox mechanism.

Alternative considered: build a chroot/process-jail implementation first. Rejected because it adds more OS-specific complexity than the MVP needs.

### Decision: Each shell invocation gets an ephemeral workspace with explicit mounts

For `sandbox-only`, the daemon creates a per-invocation workspace rooted under a configured sandbox scratch directory. The request may bind a project working tree or session directory as read-only or read-write according to the resolved file/shell policy, plus a dedicated writable scratch/output directory owned by the sandbox invocation.

The effective working directory inside the sandbox mirrors the project path semantics of host execution, but only through explicitly mounted paths. Commands do not run against the full daemon host filesystem.

Rationale: operators still need project-relative commands to work, but the isolation boundary must be explicit and inspectable.

Alternative considered: mount the entire project tree and host home directory for convenience. Rejected because it collapses most of the value of sandboxing.

### Decision: Sandbox execution is network-denied by default

The sandbox backend defaults to no outbound network access. If a future change needs networked sandbox execution, it should add explicit policy and diagnostics rather than silently inheriting host networking.

Rationale: a safer shell mode should meaningfully reduce exfiltration and remote-command blast radius. Network isolation is one of the simplest high-value defaults.

Alternative considered: allow normal network access so existing shell scripts work unchanged. Rejected because it weakens the core security benefit and hides a major trust boundary.

### Decision: Backend unavailability is a runtime error and a doctor failure, not a fallback

If shell mode resolves to `sandbox-only` and the configured backend is unavailable, unhealthy, or misconfigured, the shell tool returns a failure explaining that the sandbox runner is unavailable. The CLI doctor reports this as an error when sandbox shell is configured. Startup may either fail or surface a blocking diagnostic depending on the command surface, but execution must never widen to `host-allowed` automatically.

Rationale: Netclaw's constitution explicitly forbids silent fallback paths for security-sensitive behavior.

Alternative considered: transparently run on host when the sandbox is down. Rejected because it turns a safer configuration into a misleading one.

### Decision: Sandbox lifecycle is observable but not persisted as new domain state

Sandbox runs emit the same user-visible tool activity lifecycle and audit trail as other tool calls, with extra metadata for backend kind, container/image reference, and failure category where helpful. This metadata lives in logs, diagnostics, and tool result payloads rather than new persisted actor entities.

Rationale: shell execution is already modeled as transient tool activity. Adding durable sandbox entities would increase complexity without clear product value.

Alternative considered: persist sandbox run records as first-class domain objects. Rejected for MVP because existing logs/audit surfaces are sufficient.

### Decision: Recovery includes deterministic cleanup of stale sandbox artifacts

The runner must clean up containers/processes and ephemeral workspace directories after each invocation. On daemon startup, a lightweight cleanup pass may remove stale sandbox workspaces or orphaned runner artifacts created by prior crashes.

Rationale: isolated execution that leaks orphaned containers or scratch directories becomes an operational liability over time.

Alternative considered: rely entirely on runtime auto-remove flags without host cleanup. Rejected because crash paths and partial launches can still leave residue behind.

## Risks / Trade-offs

- [Risk] Container runtime availability differs between dev hosts and deployment targets. -> Mitigation: make backend validation explicit in doctor/status output and keep `host-allowed` as the existing opt-in path for personal deployments.
- [Risk] Project commands may fail inside the sandbox because required binaries or files are not mounted into the image. -> Mitigation: define a minimal operator-visible sandbox image contract and return actionable error details when prerequisites are missing.
- [Risk] Network-disabled sandboxes may surprise users expecting package managers or remote fetches to work. -> Mitigation: document the default clearly and require a future explicit policy change for networked sandbox execution.
- [Risk] Scratch workspace/mount rules may diverge from existing file access policy and confuse operators. -> Mitigation: derive mounts from the same resolved audience/profile policy and surface the effective roots in diagnostics.
- [Risk] Cleanup bugs could leak containers or disk usage. -> Mitigation: require auto-cleanup on success/failure plus a startup orphan sweep and focused integration tests.

## Migration Plan

1. Add sandbox configuration types and schema entries without changing existing defaults.
2. Introduce the sandbox runner abstraction and a container-backed implementation behind `sandbox-only` mode.
3. Update shell authorization/execution so `sandbox-only` routes to the isolated runner and `host-allowed` keeps the current host path.
4. Add doctor/status reporting for sandbox backend health, image/runtime validation, and configured shell mode.
5. Add integration tests for success, backend failure, timeout, and no-fallback behavior.
6. Update operator docs/runbooks to explain how to prepare the sandbox backend.

Rollback strategy:

- Operators can switch shell mode back to `host-allowed` or `off` if sandbox execution regresses.
- The code path should remain additive so removing the sandbox runner registration restores current behavior for non-`sandbox-only` deployments.
- Rollback must preserve the no-fallback rule: a broken sandbox deployment reverts by explicit operator config change, not automatic host execution.

## Open Questions

- Should v1 support only Docker, or should the config model reserve a second container runtime immediately?
- What is the minimum sandbox image contract: POSIX shell only, or a richer toolbox with git/dotnet/bash?
- Do we want startup failure for daemon boot with invalid `sandbox-only` config, or is a blocking doctor/status state sufficient as long as execution remains denied?
- How much of the host project tree should be writable inside the sandbox versus forcing all writes into a dedicated scratch mount?
