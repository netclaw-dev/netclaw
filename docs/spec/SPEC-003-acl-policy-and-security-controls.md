# SPEC-003: ACL Policy and Security Controls

Source PRDs: `PRD-001`, `PRD-002`

## Purpose

Specify ACL schema behavior and enforcement points for inbound interactions,
tool access, and ambient channel behavior.

## Policy Model

- default deny baseline
- explicit channel and sender allow rules
- per-channel `require_mention` behavior
- optional data and tool grants with explicit allow semantics

## Enforcement Points

1. inbound message classification (DM, channel, threaded channel)
2. sender authorization check
3. channel policy check
4. mention/ambient rule check
5. tool/data grant check at execution time

## Ambient Channel Rules

- ambient mode is opt-in per channel
- when acting in ambient mode, Netclaw starts a new Slack thread rooted at the
  trigger message and maps a new session key

## Fail-Closed Rules

- invalid ACL schema -> startup failure
- policy engine exception -> deny result
- missing grant for requested tool/data -> deny result

## Audit Events

- policy allow/deny decision (with reason)
- privileged approval decisions
- exposure mode and binding mode at startup
