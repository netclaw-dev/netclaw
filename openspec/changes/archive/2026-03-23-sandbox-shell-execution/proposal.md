## Why

`SandboxOnly` already exists in Netclaw's shell policy model, but today it is only a reserved mode with no executable backend. That leaves a gap between the documented security contract and the runtime behavior just as Netclaw starts carrying richer trust-context policy and stricter shell gating.

This change gives `sandbox-only` a real implementation path so operators can enable shell access without granting full host execution to the session. It also lets future public or mixed-trust deployments rely on an isolated runner instead of blocking on a later security redesign.

## What Changes

- Add a real sandbox shell execution path for the existing `sandbox-only` mode, with explicit runner configuration, lifecycle, and failure behavior.
- Define how Netclaw stages command execution into an isolated workspace, captures stdout/stderr, enforces timeouts, and returns execution errors to the LLM without falling back to host execution.
- Add operator-facing validation and diagnostics so `sandbox-only` configuration is only considered healthy when the configured sandbox backend is present and usable.
- Clarify filesystem, working-directory, and network posture for sandboxed commands so the security contract is explicit and testable.
- Keep `host-allowed` behavior unchanged for owner-operated personal deployments; this change only adds the isolated path and the rules that select it.

## Capabilities

### New Capabilities
- `netclaw-shell-sandbox`: Isolated shell runner configuration and execution contract for `sandbox-only` mode.

### Modified Capabilities
- `netclaw-tools`: Change shell execution requirements so `sandbox-only` is executable through the isolated runner instead of remaining a reserved non-executable mode.
- `netclaw-gateway-security`: Change shell boundary requirements so sandbox execution has explicit isolation guarantees, validation rules, and no-fallback failure semantics.
- `netclaw-cli`: Add doctor and status expectations for sandbox backend availability and misconfiguration reporting.

## Impact

- Affected systems: shell tool execution, tool access policy, configuration binding/schema, doctor diagnostics, onboarding/status messaging, and future trust-context enforcement for higher-risk sessions.
- Affected code areas: `Netclaw.Actors` tool execution pipeline, `Netclaw.Configuration` shell/sandbox options, `Netclaw.Cli` diagnostics, and any host integration used to launch isolated runners.
- Operational/security impact: `sandbox-only` becomes a usable safer alternative to host shell; missing or broken sandbox backends must fail closed and never widen to `host-allowed` automatically.
- Source PRDs: `PRD-001` (FR-011 tool access) and `PRD-002` (SEC-009 shell execution boundaries).
