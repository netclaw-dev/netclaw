# PRD-002: Gateway Security Envelope

## Status

- State: Draft for execution (revised)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Revised: 2026-02-21 (expanded for tool execution, self-config safety)
- Depends on: `PRD-001`

## Goal

Define the minimum security controls required for shipping Netclaw MVP without
introducing unsafe defaults. This covers inbound message policy, tool execution
policy, self-configuration safety, and exposure controls.

## Security Principles

1. Default deny over default allow.
2. Explicit operator intent for any increased exposure.
3. Fail closed on invalid configuration.
4. Tool access is policy mediated, not model mediated.
5. Keep trust boundaries simple and inspectable.
6. Self-configuration changes are validated before write.

## Requirements

### SEC-001 Policy Gate on Every Inbound Interaction

Every Slack message considered for processing must pass ACL checks for channel,
sender, and interaction mode.

### SEC-002 Mention and Ambient Policy Controls

Channels must explicitly declare whether mention is required. Ambient mode may
act without mention only where policy allows. Ambient monitoring is post-MVP
but the policy model must support it from day one.

### SEC-003 Data and Tool Grant Controls

Data access and tool invocation must be checked against configured grants before
execution. Tool categories for MVP:

- `shell` — command execution (highest risk)
- `web_search` — web search API calls
- `web_fetch` — URL content retrieval
- `github` — GitHub CLI operations
- `mcp:{server_name}` — MCP server tool invocation
- `config_write` — self-modification of agent config files
- `schedule_write` — creation/modification of scheduled tasks

Each grant specifies allowed senders and channels. Missing grant = deny.

### SEC-004 Startup Validation

Runtime startup must fail when ACL or security-critical configuration is
missing, invalid, or contradictory.

### SEC-005 Exposure Policy

Gateway network exposure defaults to loopback-only. Any broader exposure
requires explicit operator configuration.

#### Exposure Modes (MVP)

- `local`: loopback only (default)
- `tailscale-serve`: tailnet-only access
- `tailscale-funnel`: public internet exposure (high risk, explicit opt-in)
- `cloudflare-tunnel`: public internet exposure behind Cloudflare Access policy

Public modes (`tailscale-funnel`, `cloudflare-tunnel`) require explicit
authentication and elevated warning status in diagnostics.

### SEC-006 Pairing and Approval Surfaces

Privileged operations must be explicitly approved through trusted operator
channels or preconfigured owner policy.

Pairing must be required before first privileged browser-session action.

### SEC-007 Auditability

Policy denies, privileged approvals, tool invocations, and exposure settings
must be auditable in operator-facing diagnostics. Tool invocation audit records
shall include: tool name, invoking session, timestamp, and allow/deny result.

### SEC-008 Self-Configuration Safety

When the agent modifies its own configuration files (FR-014):

- Config changes must be validated before being written to disk.
- Invalid config changes must be rejected with an explanation to the user.
- ACL and security policy files cannot be modified by the agent through
  conversation — only through CLI or direct file edit by the operator.
- Agent can modify: personality, instructions, user preferences, project
  registry, environment inventory, and scheduled tasks.
- Agent cannot modify: ACL rules, exposure policy, tool grants, or security
  settings.

### SEC-009 Shell Execution Boundaries

Shell execution (when granted) operates under these constraints:

- Commands run as the Netclaw process user (no privilege escalation).
- Execution timeout is configurable (default: 60 seconds).
- Output is truncated to a configurable limit to prevent context flooding.
- No interactive commands (stdin is closed).
- Working directory is the registered project path or a configured scratch dir.

## Threat Model Scope (MVP)

In scope:

- malicious or unknown sender in allowed platform
- prompt-injection attempts via inbound messages
- accidental over-exposure from misconfiguration
- misuse of high-trust tools by over-broad policy grants
- agent self-modification introducing inconsistent or dangerous state

Out of scope (deferred):

- full marketplace supply-chain scanning
- enterprise SSO and federated identity controls
- distributed policy propagation across services
- sandboxed tool execution (formal isolation)

## Acceptance Criteria

1. Unknown sender in restrictive channel cannot trigger model execution.
2. Ambient channel behavior only activates where explicitly configured.
3. Tool execution attempts without grant return policy denial.
4. Invalid ACL file prevents successful host start.
5. Diagnostics expose effective exposure mode and latest policy denies.
6. Public exposure mode without access policy fails validation.
7. Shell execution respects timeout and output limits.
8. Agent cannot modify ACL or security config through conversation.
9. Tool invocation audit trail is available in diagnostics.

## Cross-References

- MVP scope: PRD-001
- CLI diagnostics: PRD-004
- Tool access: PRD-006 (MCP), PRD-007 (local tools)
- Self-configuration: PRD-007
