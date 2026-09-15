## ADDED Requirements

### Requirement: Teams adapter supplies explicit trusted input

The Teams adapter SHALL translate SDK-authenticated Teams activities into
Netclaw-owned immutable input contracts before actor dispatch. Microsoft SDK
types SHALL NOT reach Netclaw session actors. Each dispatched activity SHALL
carry audience, principal, boundary, provenance, sender identifier, tenant
identifier, conversation identifier, scope, activity/idempotency identifier,
received timestamp, root/reply metadata when applicable, validated mention
state when applicable, and sanitized attachment metadata when applicable.

#### Scenario: Authenticated Teams message reaches a session without SDK types

- **WHEN** an allowed Teams message is accepted
- **THEN** its session dispatch uses only a Netclaw-owned immutable contract
- **AND** that contract has complete required trust context

#### Scenario: Malformed Teams activity is rejected at the boundary

- **WHEN** an authenticated activity lacks a required message, sender, tenant,
  conversation, supported scope, or activity/idempotency identifier
- **THEN** it is rejected with a safe boundary reason
- **AND** the translator does not synthesize a tenant or activity identifier
- **AND** no conversation actor, binding actor, or model turn is created
