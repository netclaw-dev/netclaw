# Proposal: Design Ops Console and CLI v1

## Source PRDs

- `PRD-003-operator-ux-ops-console.md`
- `PRD-004-cli-onboarding-and-config.md`

## Why

Operators need a practical control plane from day one. Without clear UI and CLI
contracts, implementation may drift into ad-hoc tooling with poor security and
diagnostics coverage.

## What Changes

1. Specify ops-console information architecture and interaction contracts.
2. Define CLI command surface and safety semantics.
3. Ensure UI and CLI parity for critical operations and diagnostics.

## Scope

In scope:

- planning artifacts and mockups
- OpenSpec capability updates for operator UI and CLI

Out of scope:

- full UI runtime implementation
- final CLI command implementation

## Impact

- faster and safer operator onboarding
- lower operational risk with clearer diagnostics and policy visibility
- concrete interface contract for implementation phase
