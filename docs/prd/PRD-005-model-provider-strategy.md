# PRD-005: Model Provider Strategy

## Status

- State: Draft for execution (revised)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Revised: 2026-02-21 (MEAI abstraction, primary+fallback model)
- Revised: 2026-05-27 (No-Op chat client fallback for degraded startup)
- Depends on: `PRD-001`, `PRD-004`

## Goal

Ship Netclaw with OpenRouter as the default provider using
`Microsoft.Extensions.AI` as the pluggable abstraction layer. Support a
configurable primary model and fallback model for resilience.

## Product Outcomes

1. First-run onboarding works with OpenRouter out of the box.
2. Additional providers can be configured by operator choice.
3. Provider behavior is observable and diagnosable from CLI.
4. Fallback model activates when primary is unavailable.

## Provider Architecture

### Microsoft.Extensions.AI (MEAI) Abstraction

All LLM interactions flow through `Microsoft.Extensions.AI` interfaces
(`IChatClient`). This provides:

- Provider-agnostic tool calling
- Consistent streaming and non-streaming APIs
- DI-friendly registration
- Middleware pipeline (logging, caching, rate limiting)

The session actor receives an `IChatClient` — it never knows or cares which
provider backs it.

### Primary + Fallback Model

Configuration specifies:

- **Primary model**: used for all normal interactions
- **Fallback model**: used when primary fails (rate limit, timeout, outage)

Fallback activation is automatic and logged. The agent reports which model
served the response when asked.

### Sub-Agent Model Routing (Post-MVP)

Future: cheaper/faster models for high-token tasks (summarization, search
result processing) while reserving the primary model for reasoning. This
requires the framework to support per-request model selection. Deferred.

## Requirements

### MP-001 Default Provider

OpenRouter SHALL be the default provider presented by onboarding and sample
configuration.

### MP-002 Provider Abstraction

Runtime SHALL use `Microsoft.Extensions.AI` `IChatClient` as the provider
abstraction. Provider selection is a configuration concern, not an actor
concern.

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

CLI diagnostics SHALL report effective provider, model, fallback status, and
health state (reachable, auth error, rate limited, unknown failure).

### MP-006 Local Smoke Test Path

The project SHALL support local smoke tests against an Ollama endpoint for
integration confidence without making live-provider calls mandatory.

### MP-007 CI Provider Independence

Automated test suites required by CI/CD SHALL pass without requiring any live
model provider credentials or network access to external inference services.

### MP-008 Local Dev Ollama Profile

The default local smoke profile SHALL target the Tailscale-reachable Ollama
server `my-gpu-server` (`http://my-gpu-server:11434`) and use `qwen3:30b` (fallback
`qwen3:14b`).

### MP-009 Primary + Fallback Configuration

Configuration SHALL support specifying both a primary and fallback model:

```json
{
  "provider": "openrouter",
  "primary_model": "anthropic/claude-sonnet-4",
  "fallback_model": "anthropic/claude-haiku-4",
  "fallback_on": ["rate_limit", "timeout", "provider_error"]
}
```

Fallback activation SHALL be logged and visible in diagnostics.

### MP-010 Tool Calling Support

The provider abstraction SHALL support tool/function calling through MEAI's
built-in tool calling API. Tool definitions are registered at session startup
based on policy grants.

### Degraded startup (No-Op chat client)

If provider/model configuration validation reports **no provider configured**
(e.g., empty `Providers` section, missing or incomplete `Models:Main`, or
`Models:Main` references an unconfigured provider), daemon startup SHALL
succeed in a degraded mode. Bound object defaults such as
`local-ollama/qwen3:30b` SHALL NOT count as explicit operator configuration
unless `Models:Main:Provider` and `Models:Main:ModelId` are present in config.
The host registers a No-Op `IChatClient` that returns a fixed banner beginning
with `"No valid model configuration detected."` and lists the recovery commands
(`netclaw doctor`, `netclaw init` for first-time provider/model setup,
`netclaw model` when a provider already exists, or manual config repair). The No-Op client
SHALL NOT contact any external service and SHALL NOT emit tool calls.

Malformed provider configuration (declared provider missing required
credentials or `Type`, schema violations, or explicit `Fallback` / `Compaction`
roles that are incomplete or reference unconfigured providers) remains a
**fatal** startup error — only the "no provider configured" outcome selects the
No-Op fallback. Recovery from degraded mode requires a daemon restart; live
config swap is out of scope. `netclaw doctor` reports the state as a
**warn**-level "Chat Client" item.

## Non-Goals (MVP)

- Automated cross-provider failover logic (beyond primary/fallback)
- Dynamic per-turn provider routing
- Provider marketplace/plugin loading
- Sub-agent model routing

## Acceptance Criteria

1. Guided onboarding creates valid OpenRouter config by default.
2. Operator can switch configured provider through CLI/config update path.
3. Runtime diagnostics show current provider/model and last provider error.
4. Local smoke tests can run against configured Ollama endpoint when enabled.
5. CI validation pipeline passes with provider mocks/fakes only.
6. Fallback model activates when primary is unreachable.
7. Tool calling works through MEAI abstraction.
8. Daemon starts in degraded mode with No-Op chat client when no provider
   is configured; doctor reports the state as a warn-level item; chat turns
   return the fixed recovery banner instead of crashing.

## Cross-References

- MVP scope: PRD-001
- CLI onboarding: PRD-004
- Tool integration: PRD-006 (MCP tools), PRD-007 (local tools)
