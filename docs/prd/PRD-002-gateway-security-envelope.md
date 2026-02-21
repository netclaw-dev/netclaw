# PRD-002: Gateway Security Envelope

## Status

- State: Draft for execution
- Owner: Netclaw engineering
- Date: 2026-02-21
- Depends on: `PRD-001`

## Goal

Define the minimum security controls required for shipping Netclaw MVP without
introducing unsafe defaults.

## Security Principles

1. Default deny over default allow.
2. Explicit operator intent for any increased exposure.
3. Fail closed on invalid configuration.
4. Tool access is policy mediated, not model mediated.
5. Keep trust boundaries simple and inspectable.

## Requirements

### SEC-001 Policy Gate on Every Inbound Interaction

Every Slack message considered for processing must pass ACL checks for channel,
sender, and interaction mode.

### SEC-002 Mention and Ambient Policy Controls

Channels must explicitly declare whether mention is required. Ambient mode may
act without mention only where policy allows.

### SEC-003 Data Grant Controls

Data/tool access must be checked against configured grants before execution.

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

Policy denies, privileged approvals, and exposure settings must be auditable in
operator-facing diagnostics.

## Threat Model Scope (MVP)

In scope:

- malicious or unknown sender in allowed platform
- prompt-injection attempts via inbound messages
- accidental over-exposure from misconfiguration
- misuse of high-trust tools by over-broad policy grants

Out of scope (deferred):

- full marketplace supply-chain scanning
- enterprise SSO and federated identity controls
- distributed policy propagation across services

## Acceptance Criteria

1. Unknown sender in restrictive channel cannot trigger model execution.
2. Ambient channel behavior only activates where explicitly configured.
3. Tool execution attempts without grant return policy denial.
4. Invalid ACL file prevents successful host start.
5. Diagnostics expose effective exposure mode and latest policy denies.
6. Public exposure mode without access policy fails validation.
