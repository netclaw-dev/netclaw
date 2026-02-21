# SPEC-010: Testing and Smoke Strategy

Source PRDs: `PRD-001`, `PRD-005`, `PRD-004`

## Purpose

Define test categories so CI remains provider-independent while local smoke
tests can validate real provider integrations.

## Test Categories

### Category A: Unit Tests (CI required)

- pure logic and data model tests
- no network, no external services

### Category B: Actor Integration Tests (CI required)

- actor lifecycle and persistence behavior using in-memory/fake dependencies
- provider behavior simulated via fake chat client/provider abstractions

### Category C: Contract Tests (CI required)

- provider adapter contract behavior against fakes/stubs
- ACL/policy behavior around tool and provider invocations

### Category D: Live Smoke Tests (CI optional)

- explicit opt-in tests using real endpoints (for example, local Ollama)
- intended for developer or pre-release validation

## CI Rules

- required CI pipeline executes categories A-C only
- CI must pass without provider credentials
- live smoke tests are excluded by default from required CI jobs

## Smoke Rules

- smoke tests require explicit command invocation
- smoke tests fail fast with clear remediation if endpoint is unreachable
- smoke tests produce concise health report for provider connectivity

## Local Smoke Profile (Developer Default)

- provider: `ollama`
- endpoint: `http://big-gpu:11434` (Tailscale network)
- model: `qwen3:30b`
- fallback model: `qwen3:14b`

These settings are for local development and pre-release validation only, not
required CI.

Reference profile snippet:

- `docs/spec/examples/local-dev-provider-profile.jsonc`
