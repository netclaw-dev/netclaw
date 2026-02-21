# PRD-005: Model Provider Strategy

## Status

- State: Draft for execution
- Owner: Netclaw engineering
- Date: 2026-02-21
- Depends on: `PRD-001`, `PRD-004`

## Goal

Ship Netclaw with OpenRouter as the default provider while preserving a clean
path to support multiple model providers without architecture churn.

## Product Outcomes

1. First-run onboarding works with OpenRouter out of the box.
2. Additional providers can be configured by operator choice.
3. Provider behavior is observable and diagnosable from CLI/ops console.

## Requirements

### MP-001 Default Provider

OpenRouter SHALL be the default provider presented by onboarding and sample
configuration.

### MP-002 Provider Abstraction

Runtime SHALL use a provider abstraction that allows selecting a provider by
name and model identifier at configuration time.

### MP-003 Initial Provider Set

MVP SHALL support at least:

- OpenRouter (default)
- Anthropic direct
- OpenAI direct
- Ollama (local OpenAI-compatible endpoint for smoke testing)

Additional providers can be added post-MVP without changing session actor
contracts.

### MP-004 Credential Validation

CLI validation SHALL verify provider-specific required configuration and expose
clear remediation steps on failure.

### MP-005 Provider Health Diagnostics

CLI and ops diagnostics SHALL report effective provider, model, and health state
(reachable, auth error, rate limited, unknown failure).

### MP-006 Local Smoke Test Path

The project SHALL support local smoke tests against an Ollama endpoint for
integration confidence without making live-provider calls mandatory.

### MP-008 Local Dev Ollama Profile

The default local smoke profile SHALL target the Tailscale-reachable Ollama
server `big-gpu` and use a high-quality model profile suitable for 24 GB VRAM.

### MP-007 CI Provider Independence

Automated test suites required by CI/CD SHALL pass without requiring any live
model provider credentials or network access to external inference services.

## Non-Goals (MVP)

- automated cross-provider failover logic
- dynamic per-turn provider routing
- provider marketplace/plugin loading

## Acceptance Criteria

1. Guided onboarding creates valid OpenRouter config by default.
2. Operator can switch configured provider through CLI/config update path.
3. Runtime diagnostics show current provider/model and last provider error.
4. Local smoke tests can run against configured Ollama endpoint when enabled.
5. CI validation pipeline passes with provider mocks/fakes only.
6. Local smoke profile documents a recommended model and fallback model.
