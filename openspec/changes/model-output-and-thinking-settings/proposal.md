## Why

An operator must know each provider's own switch to limit the reply length or
to set the thinking depth. Aaron's requirement: "I shouldn't need to know the
different switches for all these different inference providers."

Today, Netclaw has no per-model setting for either value. Only memory curation
sets an output limit. Thus each Anthropic call except curation uses the SDK
default of 1024 output tokens. Since `fail-truncated-reply-and-cap-thinking`,
a reply that hits that limit fails the turn.

Source PRDs: `PRD-005` (MP-002 Provider Abstraction, MP-009 Primary + Fallback
Configuration) and `PRD-004` (CLI onboarding and config).

## What Changes

- A model definition gets two optional, provider-neutral fields:
  - `MaxOutputTokens`: a positive integer.
  - `Thinking`: one of `Off`, `Low`, `Medium`, `High`, `Max`.
- Each provider maps the two fields onto its own request switch. The existing
  reasoning-suppression dialect becomes the thinking dialect, and it owns this
  mapping. Netclaw adds no second mechanism.
- A self-hosted OpenAI-compatible provider gets its thinking dialect once per
  provider. `netclaw model set` detects it where the endpoint shows it. Else
  the operator sets `Providers:<name>:VendorOptions:ThinkingDialect`.
- The capability resolvers report whether a model thinks, which levels it
  accepts, and its maximum output. Netclaw offers a level only when the
  dialect can send it and the model accepts it. It never guesses a level.
  A new native Anthropic resolver reads the Anthropic Models API.
- Netclaw rejects a level that is not offered. `model set`, the TUI model
  manager, `doctor`, and startup validation reject it. Netclaw never maps a
  level to a different level.
- A call-site value wins over the model value. Memory curation keeps its own
  4096-token limit and its reasoning suppression.
- **Behavior change for every provider** (Aaron's decision): when
  `MaxOutputTokens` is unset, Netclaw sends a default of 32000 tokens. For each
  call, Netclaw clamps the default to the model's known maximum output and to
  the context window minus the estimated prompt. An explicit value is sent as
  is. The release notes and the `netclaw-operations` skill state the change.
- Each failover attempt gets its own copy of the request options. This change
  fixes issue #2681.
- `netclaw model set` gets `--max-output-tokens`, `--clear-max-output-tokens`,
  `--thinking`, and `--clear-thinking`. `netclaw model list` and the TUI role
  table get "Max out" and "Thinking" columns. `P` in the TUI model manager
  opens a properties panel for the selected role's definition.
- The startup model line and the per-call debug line show the effective limit
  and thinking level, with the source of each value.

In scope for MVP: the two fields, the per-provider mapping, the CLI and TUI
editors, doctor validation, and the #2681 fix.

Out of scope: a per-role or per-call-site override, a thinking token budget, a
hard-coded model table, a context-budget reserve for the output limit, and a
models.dev resolver. The new capability fields are the targets of that later
resolver.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `named-model-definitions`: a definition owns the output limit and the
  thinking level.
- `netclaw-model-providers`: provider-neutral mapping through the thinking
  dialect, the clamped default output limit, the thinking capability,
  rejection of levels that are not offered, call-site precedence, failover
  option isolation, and log visibility.
- `netclaw-cli`: `model set` flags, `model list` columns, and the TUI
  properties panel, with validation before persistence.

## Impact

- Code: `ModelReference`, `ModelEntryWriter`, `ModelCommand`, the TUI model
  manager, `ProviderPluginFactory` (the per-model client stack), the provider
  descriptors and plugins, `OpenAiCompatibleChatClient`,
  `OpenRouterReasoningExcludePolicy`, `ResolvedModelCapabilities` and its
  resolvers (OpenRouter, Ollama, OpenAI-compatible, a new Anthropic resolver),
  `ModelConfigurationValidation`, and the config schema.
- Docs: `docs/spec/GLOSSARY.md` (three terms), SPEC-008, `RELEASE_NOTES.md`,
  and the `netclaw-operations` system skill.
- Security: no new grant, tool, or authorization context. The change does not
  touch `docs/architecture/tool-authorization.md`.
- Operations:
  - Each provider now receives an output limit of up to 32000 tokens when none
    is configured. Anthropic replies are no longer cut at 1024 tokens. The
    cost per call can increase.
  - A hand-edited unsupported level now fails startup with a remediation.
  - A fallback provider no longer receives the first provider's dialect
    fields.
