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
- DwarfStar (ds4) — local DeepSeek V4 engine via the `openai-compatible`
  provider type; its OpenRouter-shaped `context_length` metadata is read by a
  dedicated backend strategy in the capability resolver

## Configuration Model

- one active provider profile at runtime
- provider-specific credential fields
- default model configured per provider profile

## Output Limit and Thinking Level

A model definition can hold two provider-neutral settings. The operator sets
them with `netclaw model set` or the `P` panel of the TUI model manager. See
the [engineering glossary](GLOSSARY.md) for "thinking level", "thinking
dialect", and "thinking capability".

- `MaxOutputTokens`: a positive integer, below `ContextWindow` when that is set.
- `Thinking`: `Off`, `Low`, `Medium`, `High`, or `Max`.

A call-site value wins over the definition. Memory curation keeps its own
4096-token limit and its reasoning suppression.

### Default output limit

When no value is set, each call sends 32000, capped and clamped per call:

```text
value  = min(32000, model max output when known)
window = hosted:      configured ContextWindow ?? resolved window (a catalog window counts)
         self-hosted: configured ContextWindow ?? window of the endpoint's own probe
                      no window -> no limit field
margin = max(1024, window / 20)
value  = min(value, max(1024, window - prompt estimate - margin))   // when the window is known
```

The prompt estimate is the compaction rule (characters / 4) and counts the tool
schemas. A self-hosted endpoint ignores a catalog model maximum. A self-hosted
server must use the `ollama` or `openai-compatible` provider type; a hosted type
(`deepseek`, `openrouter`, `veniceai`) that points at your own server is treated
as hosted. An explicit value is sent as is. Each definition resolves its
capability against the chain of its own provider.

### Provider mapping

| Provider | Output limit | Off | Low / Medium / High | Max |
|---|---|---|---|---|
| Anthropic | `max_tokens` | `thinking.type: disabled` | adaptive + `output_config.effort` | `output_config.effort: max` |
| OpenAI, Codex, Copilot (Responses) | `max_output_tokens` | `reasoning.effort: none` | `reasoning.effort` | `xhigh` |
| OpenRouter | `max_completion_tokens` | `reasoning.effort: none` | `reasoning.effort`, with `exclude` kept | `max` |
| Venice | `max_completion_tokens` | `reasoning_effort: none` | `reasoning_effort` | `max` |
| DeepSeek | `max_tokens` | `thinking.type: disabled` | `reasoning_effort` low / high (the API has no medium) | `max` |
| Ollama | `num_predict` | `think: false` | `think: "low"` / `"medium"` / `"high"` | not offered |
| OpenAI-compatible | `max_tokens` | per `ThinkingDialect` | per `ThinkingDialect` | per `ThinkingDialect` |

An OpenAI-compatible provider reads its dialect from
`Providers:<name>:VendorOptions:ThinkingDialect` (`ChatTemplateKwargs`,
`ReasoningEffort`, `DeepSeek`, or `None`). `netclaw model set` saves
`ReasoningEffort` when it detects llama.cpp. Without a value, no level is
offered, and only the output limit works.

### Offered levels

Offered levels = (levels the dialect can send) ∩ (levels the model accepts).
The model capability comes from the provider probe of `netclaw model set` and
the TUI. An `Unknown` capability offers only `Off`, and only for a body-field
dialect; the Native dialect offers no level for an unknown model. Netclaw rejects a level
that is not offered, and it lists the offered levels. Startup and doctor check
only the dialect. A provider that rejects a level returns a loud error.

### Failover isolation

Each model client copies the caller's options before any wrapper changes them.
A fallback candidate never sees the request fields of a failed candidate.

## Validation and Diagnostics

- CLI validates required fields for selected provider
- diagnostics report selected provider + model + last error state
- startup fails on missing required provider credentials
- startup fails on a thinking level that the provider dialect cannot send, and
  on an invalid `VendorOptions:ThinkingDialect` value
- the startup log has one line for each model client with its limit, window,
  level, dialect, and capability; each call logs one Debug line with the
  effective limit and level and the source of each
- local smoke profile supports endpoint-driven provider checks (for Ollama)

## Local Dev Defaults (Non-CI)

- preferred smoke endpoint: `http://my-gpu-server:11434`
- preferred smoke model: `qwen3:30b`
- fallback smoke model: `qwen3:14b`

Rationale: `my-gpu-server` is available on Tailscale for local development and has
enough VRAM for a stronger coding-oriented model profile.

## Testing Constraints

- CI-required tests use provider fakes/mocks and do not require live model
  credentials
- live-provider smoke tests are optional and opt-in (local/dev execution)

## Non-Goals (MVP)

- automatic provider failover
- multi-provider load balancing
- per-channel dynamic provider selection
