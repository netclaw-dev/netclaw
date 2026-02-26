# Proposal: Add Provider Smoke and CI Independence

## Source PRDs

- `PRD-005-model-provider-strategy.md`
- `PRD-001-netclaw-mvp.md`
- `PRD-004-cli-onboarding-and-config.md`

## Why

Developers need confidence checks against real local endpoints (Ollama), while
the OSS-friendly CI pipeline must not require live provider credentials.

## What Changes

1. Add requirements for optional live smoke tests against local providers.
2. Add requirements that CI-required tests remain provider-independent.
3. Add CLI and capability-level contracts for smoke command semantics.
4. Capture local-dev defaults for the Tailscale Ollama host `big-gpu`.

## Scope

In scope:

- planning/specification updates only

Out of scope:

- implementing full smoke test runner in this change

## Impact

- clearer local validation workflow
- CI remains deterministic and contributor-friendly
