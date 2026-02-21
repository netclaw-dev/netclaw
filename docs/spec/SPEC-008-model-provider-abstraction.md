# SPEC-008: Model Provider Abstraction

Source PRDs: `PRD-005`, `PRD-001`

## Purpose

Define a provider abstraction that starts with OpenRouter and supports multiple
providers without changing core session actor contracts.

## Provider Contract

Runtime provider implementation must expose:

- provider identity (`name`)
- model identity (`model`)
- request execution (`chat completion`)
- health check status
- normalized error mapping

Session actors depend on provider-neutral chat client behavior.

## MVP Providers

- OpenRouter (default)
- Anthropic direct
- OpenAI direct
- Ollama via OpenAI-compatible local endpoint

## Configuration Model

- one active provider profile at runtime
- provider-specific credential fields
- default model configured per provider profile

## Validation and Diagnostics

- CLI validates required fields for selected provider
- diagnostics report selected provider + model + last error state
- startup fails on missing required provider credentials
- local smoke profile supports endpoint-driven provider checks (for Ollama)

## Local Dev Defaults (Non-CI)

- preferred smoke endpoint: `http://big-gpu:11434`
- preferred smoke model: `qwen3:30b`
- fallback smoke model: `qwen3:14b`

Rationale: `big-gpu` is available on Tailscale for local development and has
enough VRAM for a stronger coding-oriented model profile.

## Testing Constraints

- CI-required tests use provider fakes/mocks and do not require live model
  credentials
- live-provider smoke tests are optional and opt-in (local/dev execution)

## Non-Goals (MVP)

- automatic provider failover
- multi-provider load balancing
- per-channel dynamic provider selection
