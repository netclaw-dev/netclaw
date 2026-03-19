## Why

Netclaw's current security model is strong on default-deny ACLs and tool grants, but it does not yet have a first-class trust context that can consistently downgrade capabilities across deployment posture, channel exposure, source provenance, risky subtasks, memory recall, and MCP/built-in tools. This gap becomes urgent as Netclaw expands beyond private owner-operated Slack threads into public or mixed-trust environments such as Discord, webhooks, and teammate-facing personal bots.

This change establishes a cross-cutting audience and trust-context model so Netclaw can fail closed when policy is incomplete, keep prompt-injection-prone content from inheriting privileged authority, and give operators a configurable but inspectable security story before webhook and public adapters arrive.

## What Changes

- Introduce a first-class trust-context model that combines deployment posture, source/channel exposure, principal identity, payload provenance, and working-context downgrades into an effective audience/capability envelope per turn.
- Add audience-scoped policy profiles for `public`, `team`, and `personal` so channels, memories, tools, MCP servers, and output effects share the same resolved visibility and capability envelope.
- Extend memory policy beyond the current coarse `normal`/`secret` split by defining audience-aware recall rules that preserve project/domain scoping while separating public/team/personal visibility from sensitivity.
- Define operator-configurable tool/resource policy for built-in tools and MCP servers, including tool allowlists, filesystem roots, publish destinations, MCP visibility, shell execution mode policy (`off`, `sandbox-only`, `host-allowed`), and explicit downgrade rules for tainted or public-origin work.
- Add strict-default configuration behavior, recommended audience profile defaults, and diagnostics rules so missing or partial security policy resolves to less capability, with operator-facing doctor checks and explainability for effective policy.
- Clarify webhook and public-source trust evaluation so verified transport, source ownership, source visibility, event type, and payload taint are treated separately.

## Capabilities

### New Capabilities
- `netclaw-trust-context`: Cross-cutting trust-context and audience model for deriving effective exposure, capability, and approval requirements per turn.

### Modified Capabilities
- `netclaw-acl`: Replace channel/sender-only gating with trust-context-aware source admission and effective audience derivation.
- `netclaw-agent-memory`: Add audience-aware memory visibility rules and richer sensitivity guidance while keeping project/domain scoping intact.
- `netclaw-tools`: Add posture-aware built-in tool exposure/invocation policy, including shell execution mode handling and fail-closed defaults.
- `netclaw-mcp`: Add MCP capability classification and trust-context-aware exposure/invocation policy for sensitive-read, memory-safe, and future isolated-execution providers.
- `netclaw-input-adapters`: Require inbound adapters to attach enough provenance and exposure metadata for trust-context calculation across Slack, reminders, webhooks, and future Discord/public sources.
- `netclaw-cli`: Add doctor/explain surfaces for strict-default security policy validation and effective trust-context diagnostics.
- `netclaw-gateway-security`: Update gateway security requirements to define strict-default security posture, downgrade-only trust transitions, and verified-source vs tainted-payload handling.

## Impact

- Affected systems: channel adapters, session source metadata, memory recall/persistence policy, tool exposure/invocation policy, MCP server configuration, audience profile configuration, CLI/TUI onboarding, doctor diagnostics, and future webhook/public-bot routing.
- Affected code areas: `Netclaw.Configuration` schema/options, `Netclaw.Security`, `Netclaw.Actors` session/memory/tool routing, Slack channel policy, future Discord/webhook adapters, and CLI doctor/onboarding surfaces.
- Operational/security impact: incomplete policy must fail closed; public or mixed-trust deployments gain safer defaults; future sandboxed execution remains out of scope for this change but shell policy must reserve space for it.
- PRD traceability: aligns with `PRD-002` gateway security envelope, `PRD-006` MCP tool integration, and `PRD-009` unified input architecture.
