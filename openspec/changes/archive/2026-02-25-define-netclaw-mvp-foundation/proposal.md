# Proposal: Define Netclaw MVP Foundation

## Source PRDs

- `PRD-001-netclaw-mvp.md`
- `PRD-002-gateway-security-envelope.md`

## Why

Netclaw needs a stable baseline for session behavior, security posture, and
Slack transport before implementation can begin safely. The current repository
started from a build template and lacks product-aligned capability specs.

## What Changes

1. Establish OpenSpec capability specs for session lifecycle, gateway security,
   ACL behavior, and Slack Socket Mode transport.
2. Define planning artifacts that enforce default-deny and fail-closed behavior.
3. Formalize architecture boundaries between gateway adapters and session actors.

## Scope

In scope:

- requirements/specification artifacts only
- no production runtime code implementation

Out of scope:

- implementing actor logic and adapter runtime changes
- implementing web UI or CLI runtime code

## Impact

- improves implementation reliability by making requirements explicit
- reduces security drift through testable default-deny requirements
- creates a reusable planning baseline for future OpenSpec changes
