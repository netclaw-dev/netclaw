## Context

See proposal.md for the motivation. The facts below come from a survey of `dev`
at f80b5feb9.

- `ProviderPluginFactory.Create(ModelReference)` is the one place that builds a
  model client. Each client is a stack:
  `ReasoningSuppressionChatClient` -> `VendorOptionsChatClient` (optional) ->
  the SDK client. The factory has the `ModelReference` of the definition.
- `ReasoningSuppressionDialect` (`None`, `ChatTemplateKwargs`, `OllamaThink`,
  `DeepSeekThinking`) is a fixed property on each plugin. It maps one intent
  key, `netclaw:suppress-reasoning`, to a request field. Only memory curation
  sets that key.
- `RoutingChatClient` passes one `ChatOptions` object to each candidate. The
  wrappers change it in place (issue #2681).
- `OpenAiCompatibleChatClient` sends `max_tokens` when it is set (:251). It
  maps `ChatOptions.Reasoning` for the DeepSeek wire profile only (:278), and
  it maps `Low` and `Medium` to `high` without a message.
- `OpenRouterReasoningExcludePolicy` replaces the whole `reasoning` object with
  `{exclude: true}`. An effort value is lost.
- `netclaw model set` already probes the provider (`IProviderProbe`).
  `OpenAiCompatibleCapabilityResolver` already tells ds4, vLLM, llama.cpp, and
  generic servers apart.
- `CompositeCapabilityResolver` merges `ResolvedModelCapabilities(ModelId,
  InputModalities, OutputModalities, ContextWindowTokens)` from a chain. The
  first non-null value wins for each field. The chain order is the native
  probes (Ollama, OpenAI-compatible), then `OpenRouterOracleResolver`, then
  `HuggingFaceCapabilityResolver` (Program.cs ~995-1004).
- `SessionCompactionPipeline.EstimateTokens` estimates a prompt as total
  characters / 4, with media at base64 size.
- Anthropic SDK 12.50.0 sends `max_tokens` 1024 when `MaxOutputTokens` is
  unset.

## Goals / Non-Goals

**Goals:**

- One mechanism maps the two neutral settings to each provider. It extends the
  existing dialect, and it does not add a parallel one.
- A default output limit that does not break a long session or a model with a
  small output maximum.
- Netclaw offers a thinking level only when it knows that the model accepts it.
- The CLI, the TUI, doctor, and startup use one validator.
- A failover candidate never sees another candidate's request fields.

**Non-Goals:**

- No per-role or per-call-site settings. No thinking token budget.
- No hard-coded table of models.
- No reserve for the output limit in the session compaction budget.
- No TUI provider-editor field for `ThinkingDialect`. The CLI and
  `netclaw.json` set it.
- A models.dev resolver is a separate change; these fields are its targets.
- No native Venice or Copilot capability resolver in this change.

## Decisions

### D1. Settings live on the model definition, not on the role

`ModelReference` gets `int? MaxOutputTokens` and `ThinkingLevel? Thinking`.
`ThinkingLevel` is `Off | Low | Medium | High | Max`. `Max` means the highest
level that the provider offers.

- Owner: the operator, through `ModelEntryWriter`. Data: durable config.
- Why per definition: a definition already owns `ContextWindow` and the
  modalities. A valid limit and a valid level depend on the model and the
  provider, not on the role. A role switch keeps the values with the model.
- Per-role tuning: make a second definition of the same model, for example
  `sonnet-low` and `sonnet-high`.
- Alternative: per role. Rejected, because a role switch would carry a value to
  a model that cannot accept it.
- Alternative: an `On` level. Rejected. An unset level already means "the
  model's default".

### D2. One mechanism: the thinking dialect

Rename `ReasoningSuppressionDialect` to `ThinkingDialect` and extend it. The
dialect states which levels the wire can express. It translates the effective
level and the suppression intent.

| Dialect | Used by | Off | Low | Medium | High | Max |
|---|---|---|---|---|---|---|
| `Native` | Anthropic, OpenAI, Codex, Copilot, OpenRouter, Venice | yes | yes | yes | yes | yes |
| `DeepSeek` | DeepSeek; OpenAI-compatible when set | yes | yes | no | yes | yes |
| `ReasoningEffort` | OpenAI-compatible when set or detected | yes (`none`) | yes | yes | yes | no |
| `ChatTemplateKwargs` | OpenAI-compatible when set | yes | no | no | no | no |
| `OllamaThink` | Ollama | yes | yes | yes | yes | no |
| `None` | OpenAI-compatible when set | no | no | no | no | no |
| `Unresolved` | OpenAI-compatible with no value | no | no | no | no | no |

`Unresolved` is not a config value. It keeps today's suppression form
(`chat_template_kwargs`) and supports no level (D6). `Native` replaces today's
`None` for strict SDKs. It sets the standard `ChatOptions.Reasoning`, and the
SDK adapter writes the wire field. The other dialects write request-body fields
through `AdditionalProperties`, as today. `OllamaThink` writes `Off` as the
body field `think: false`, as today, and the levels through
`ChatOptions.Reasoning`. OllamaSharp maps those levels to string `think`
values (verified, D3).

The dialect is one of two filters. The other is the model's thinking
capability (D5). Netclaw offers a level only when both allow it.

The dialect resolution moves from `ILlmProviderPlugin` to `IProviderDescriptor`
as `ResolveThinkingDialect(ProviderEntry)`. The CLI and doctor have
descriptors, so they use the same resolution as the daemon.

`ReasoningSuppressionChatClient` becomes `ModelRequestSettingsChatClient`. It
is the outermost wrapper of each model client. The factory gives it the
`ModelReference`, the dialect, and the capability lookup (D4). The DeepSeek
reasoning code moves out of `OpenAiCompatibleChatClient` into the DeepSeek
dialect. The DeepSeek wire profile keeps only its message replay rules.

Per-call flow (schematic; omits the logging scope and cancellation):

```
ModelRequestSettingsChatClient.Call(messages, callerOptions):
  o = callerOptions?.Clone() ?? new ChatOptions()    // D9: own copy per candidate
  (limit, limitSource) =
      o.MaxOutputTokens        -> (it, call)
      model.MaxOutputTokens    -> (it, configured)    // sent as is, never clamped
      otherwise                -> DefaultLimit(messages)   // D4: default or default-clamped
  o.MaxOutputTokens = limit
  suppress = o.AdditionalProperties.Remove(SuppressReasoning)       // always removed
  (level, levelSource) =
      suppress and dialect sends Off in the body -> (Off, suppressed)
      suppress (Native, None)                    -> (null, suppressed)  // today's behavior
      // Unresolved: suppress -> chat_template_kwargs, as today
      o.Reasoning is set       -> (from o.Reasoning, call)
      model.Thinking           -> (it, configured)
      otherwise                -> (null, unset)
  dialect.Apply(o, level)     // throws when the dialect cannot express the level
  log.Debug(limit, limitSource, level, levelSource, dialect)
  return inner.Call(messages, o)   // inner = VendorOptions wrapper -> SDK client
```

Ownership and data:

| Decision | Owner | Data |
|---|---|---|
| Limit and level of a model | operator, via `ModelEntryWriter` | durable (`Models:Definitions`) |
| Dialect of a provider | provider descriptor | durable (`VendorOptions`) or code constant |
| Levels that a dialect expresses | static dialect map | code constant |
| Thinking capability, model max output | capability resolvers (D5) | cached for the client's lifetime |
| Dialect detection | `netclaw model set`, with the existing probe | call-local, then durable |
| Validation | one validator for CLI, TUI, doctor, startup | call-local |
| Default limit and clamp | `ModelRequestSettingsChatClient` | call-local |
| Effective values and wire fields | `ModelRequestSettingsChatClient` | call-local (copied options) |
| OpenRouter effort and exclude merge | `OpenRouterReasoningExcludePolicy` | call-local |

Alternative: a new `IThinkingMapper` per plugin. Rejected, because the dialect
already is that seam, and two seams would drift.

### D3. Per-provider mapping (verified)

Prefer the standard `ChatOptions.MaxOutputTokens` and `ChatOptions.Reasoning`
where the SDK maps them. Use a body field or a plugin-owned raw request only
where it does not. "V" marks a verified fact with its source. "U" marks an
unverified fact.

| Provider (wire) | Output limit field | Off | Low / Medium / High | Max |
|---|---|---|---|---|
| Anthropic (SDK adapter, adaptive mode) | `max_tokens` (V: SDK 12.50.0) | `thinking.type: disabled` (V) | `thinking.type: adaptive` + `output_config.effort` low / medium / high (V) | plugin sets `output_config.effort: max` in the raw request; the adapter maps `ExtraHigh` to `xhigh` and keeps a caller effort (V) |
| OpenAI API key and Codex (Responses) | `max_output_tokens` (V: M.E.AI 10.8.3) | `reasoning.effort: none` (V) | `low` / `medium` / `high` (V) | `xhigh` (U: Responses mapping of `ExtraHigh`) |
| GitHub Copilot (Responses or Chat) | `max_output_tokens` or `max_completion_tokens` (V: SDK) | effort `none` | `low` / `medium` / `high` | `xhigh` (U: Copilot accepts effort for every model) |
| OpenRouter (Chat + policy) | `max_completion_tokens` (V: SDK writes it; OpenRouter docs accept it) | policy writes `reasoning.effort: none` (V: docs) | policy writes `reasoning.effort` and keeps `exclude` (V: docs allow both) | `reasoning.effort: max` (V: docs list `max`) |
| Venice (Chat) | `max_completion_tokens` (V: docs; `max_tokens` is deprecated) | `reasoning_effort: none` (V: docs) | `reasoning_effort` low / medium / high (V: docs) | plugin sets `reasoning_effort: max` through the SDK JSON patch (V: docs list `max`) |
| DeepSeek (self-hosted client) | `max_tokens` (V: API reference) | `thinking.type: disabled` (V: docs) | `reasoning_effort` low / high; `Medium` is rejected (V: the API docs list none, low, high, and max) | `reasoning_effort: max` (V) |
| Ollama (OllamaSharp 5.4.30) | `num_predict` (V: SDK) | `think: false` (V) | `think: "low" / "medium" / "high"` through `ChatOptions.Reasoning` (V: SDK maps them; docs accept model-defined strings) | rejected: OllamaSharp maps `ExtraHigh` to `"high"` |
| OpenAI-compatible (self-hosted client) | `max_tokens` (V: :251) | per backend rows | per backend rows | per backend rows |

Facts behind the rows:

- Anthropic 12.50.0 `AsIChatClient` has `AnthropicThinkingMode.Adaptive` (the
  default) and `Extended`. In adaptive mode, `ReasoningEffort.None` sends
  `thinking.type: disabled`. `Low`, `Medium`, `High`, and `ExtraHigh` send
  `output_config.effort` `low`, `medium`, `high`, and `xhigh` (IL switch on
  `effort - 1`). The adapter does not overwrite an effort that the caller set
  in the raw request. With `MaxOutputTokens` unset, the adapter sends
  `_defaultMaxTokens` (1024).
- The OpenAI SDK 2.12.0 Chat options write `max_completion_tokens` and
  `reasoning_effort`. The M.E.AI Chat adapter maps `ExtraHigh` to `"xhigh"`.
- OpenRouter documents `max_tokens` and `max_completion_tokens` with the same
  meaning. It documents `reasoning.effort` values `none`, `minimal`, `low`,
  `medium`, `high`, `xhigh`, and `max`. A model with `reasoning.mandatory: true`
  rejects `none`.
- OllamaSharp 5.4.30 maps `MaxOutputTokens` to `num_predict`. It maps
  `ReasoningEffort.None` to `think: false`, and `Low`, `Medium`, `High` to the
  strings. It maps `ExtraHigh` to `"high"`, so Netclaw never sends `ExtraHigh`
  to Ollama.

OpenAI-compatible per-backend rows. The output limit uses `max_tokens` on each
row.

| Backend | Facts | Dialect | Off | Levels |
|---|---|---|---|---|
| llama.cpp | V (server source, master): `max_tokens` maps to `n_predict`; `max_completion_tokens` is not mapped. `reasoning_effort: none` sets `enable_thinking` false. Another value goes to the template as `chat_template_kwargs.reasoning_effort`. `chat_template_kwargs.enable_thinking` is read. Per-request `reasoning_budget_tokens` exists. `/props` has `chat_template_caps.supports_reasoning_effort`. | detected: `ReasoningEffort` | `reasoning_effort: none` | low / medium / high when `supports_reasoning_effort` is true |
| vLLM | V (docs): `reasoning_effort` low / medium / high set `enable_thinking` true, and `none` sets it false. `chat_template_kwargs` names differ by model (`enable_thinking`, `thinking`). gpt-oss levels: U. | operator: `ReasoningEffort` or `ChatTemplateKwargs` | yes | only with capability evidence (D5) |
| SGLang | U: `chat_template_kwargs.enable_thinking` for Qwen3, `max_tokens`. | operator | yes | only with capability evidence |
| ds4 (`owned_by: ds4.c`) | U: the README shows `ds4-server` and CLI flags `--think`, `--nothink`, `--think-level`. It does not document request fields. | operator only; no auto-detection | per dialect | per dialect |
| Ollama `/v1` | U: `reasoning_effort` on the OpenAI endpoint. | operator; prefer the `ollama` provider type | per dialect | per dialect |
| Generic proxy (for example LiteLLM) | U | operator: `ReasoningEffort` or `None` | per dialect | only with capability evidence |

A `chat_template_kwargs` value from the caller is merged, not replaced. The
dialect writes a new dictionary, so the caller's object does not change.

### D4. Default output limit: 32,000 with a per-call clamp

Decision by Aaron: when `MaxOutputTokens` is unset, Netclaw sends a default to
every provider. The default is 32,000 tokens. This is an intentional behavior
change for all providers. The release notes and the `netclaw-operations` skill
state it.

A flat 32,000 can fail a request. vLLM rejects a request when the prompt tokens
plus `max_tokens` exceed `max_model_len` (U: the exact error text). Some models
cap their output below 32,000.

Decision by Aaron (review of PR #2694): the window source depends on who runs
the endpoint. The provider descriptor states it (`IProviderDescriptor.IsSelfHosted`:
true for `ollama` and `openai-compatible`).

```
DefaultLimit(messages):                              // schematic
  if call value or configured value: send it as is
  modelMax = hosted ? capability.MaxOutputTokens : null   // a catalog max describes another host
  window   = model.ContextWindow                     // configured: known
             ?? hosted      ? resolved window         // a catalog window describes the same hosted model
             :  self-hosted ? probe window of this endpoint   // a catalog window is unknown
  if self-hosted and window is unknown: send NO limit field   // the server decides
  value = min(32000, modelMax ?? 32000)
  if window is known:
    prompt   = EstimateTokens(messages, tool schemas) // characters / 4
    margin   = max(1024, window / 20)
    value    = min(value, max(1024, window - prompt - margin))
  source = (value == 32000) ? default : default-clamped
```

- A hosted API can reject input + max_tokens above its window, so a hosted
  default clamps to a known window too. An unknown window keeps
  min(32000, model max).
- The startup default of 32768 is never a known window.
- The prompt estimate counts the tool schemas of the request.
- The composite resolver marks a window from a cross-provider catalog
  (`ResolvedModelCapabilities.ContextWindowFromCatalog`).
- A self-hosted server must use the `ollama` or `openai-compatible` provider
  type. A hosted type (`deepseek`, `openrouter`, `veniceai`) that points at an
  operator's own server is treated as hosted.
- The hosted model max comes from the OpenRouter catalog, except for Anthropic,
  whose Models API reports it. Copilot's own model limits are not read yet.
- A lookup without a result stands for 10 minutes (the factory's
  `TimeProvider`); then a call asks again. Only the first miss logs the
  Information line.

An explicit value (`call` or `configured`) is sent as is. Validation rejects an
explicit value that is not below the context window, or that is above a known
model maximum (D10).

Inputs and their sources:

| Input | Source | Status |
|---|---|---|
| Context window | `ModelReference.ContextWindow` (provenance configured), else the resolved window. For a self-hosted endpoint, only the window of its native probe counts; a catalog window is unknown. The startup default of 32,768 is always unknown. | existing code |
| Prompt estimate | the `EstimateTokens` rule (characters / 4), applied to the call's messages | existing code; one shared helper |
| Model max output | Anthropic Models API `max_tokens` (new native resolver) | V: SDK 12.50.0 `ModelInfo.max_tokens` |
| Model max output | OpenRouter catalog `top_provider.max_completion_tokens` | V: live catalog |
| Model max output | OpenAI Models API | V: no such field; the OpenRouter name oracle is the fallback |
| Model max output | models.dev `limit.output` (later change) | V: live catalog |
| Model max output | Venice `model_spec.maxCompletionTokens` (no resolver in this change) | V: live catalog |

The provider-reported input count of the last call is more exact, but the model
client is shared across sessions and has no session state. The margin covers
the estimate error.

Examples (margin = max(1024, window / 20)):

| Case | Window | Prompt | Model max | Sent | Source |
|---|---|---|---|---|---|
| Self-hosted, short prompt | 131,072 | 5,000 | unknown | 32,000 | `default` |
| Self-hosted, prompt near the window | 131,072 | 110,000 | unknown | 14,519 | `default-clamped` |
| Self-hosted, prompt past the floor | 32,768 | 31,000 | unknown | 1,024 (floor); a warning is logged | `default-clamped` |
| Self-hosted, unknown window | unknown | 5,000 | any | no limit field | none |
| Hosted, window known | 200,000 | 170,000 | unknown | 20,000 | `default-clamped` |
| Hosted, window unknown | unknown | 110,000 | unknown | 32,000 | `default` |
| Hosted, known model max below 32,000 | any | 5,000 | 16,384 | 16,384 | `default-clamped` |
| Explicit value | 131,072 | 110,000 | 64,000 | 64,000 | `configured` |
| Memory curation | any | any | any | 4,096 | `call` |

The capability inputs are resolved for each model client on first use. Each
definition resolves against the chain of its own provider: the daemon builds
one `CompositeCapabilityResolver` for each configured provider (its native
probe, then the catalogs), and `ProviderCapabilityResolver` selects the chain
through `IModelCapabilityResolver.ResolveAsync(ModelReference, ...)`. A result
is cached for the client's lifetime. A failed lookup, or a lookup without a
result, stands for 10 minutes: the calls use unknown inputs, and a call after
that time looks the model up again. A model ID that no role names resolves to
unknown; no other provider's chain answers for it. A network failure is a normal runtime condition, so an unknown
input is not a silent fallback. The composite keeps its early stop: when the
native probe knows the input, output, and window fields, it makes no catalog
call.

Alternative: a flat 32,000 with no clamp. Rejected, because long vLLM sessions
would fail near the window. Alternative: compute the default in the session
actor. Rejected, because sub-agents, compaction, and titles would each need the
same code.

### D5. How Netclaw knows whether a model thinks

Extend `ResolvedModelCapabilities` with two fields, and add no new system:

- `ThinkingSupport Thinking`: `Unknown | None | AlwaysOn | Controllable(levels)`.
  `levels` is a subset of `Off, Low, Medium, High, Max`. A model that thinks
  but cannot turn thinking off is `Controllable` without `Off`.
- `int? MaxOutputTokens`: the model's output maximum.

Precedence for each new field, the same order that `CompositeCapabilityResolver`
already uses:

1. The provider-native probe (what the actual server reports).
2. A catalog (the OpenRouter name oracle today; models.dev later).
3. Unknown.

A catalog resolver slots into the chain beside `OpenRouterOracleResolver`. The
merge rule "first non-null wins" becomes "first non-`Unknown` wins" for
`Thinking`.

Sources:

| Source | Signal | Maps to | Status |
|---|---|---|---|
| Anthropic Models API (new native resolver, `GET /v1/models/{id}`) | `capabilities.thinking.supported`, `.types.adaptive` / `.types.enabled`, `capabilities.effort.{low, medium, high, xhigh, max}.supported`, `max_tokens` | `Controllable` with the supported efforts; `Off` only for a model that accepts `disabled` (U: how the API reports that) | V: SDK 12.50.0 model types |
| OpenAI Models API | `id`, `created`, `owned_by` only | no signal; the OpenRouter name oracle is the fallback | V |
| OpenRouter catalog | `supported_parameters` has `reasoning`; `reasoning.mandatory`, `reasoning.supported_efforts`, `reasoning.default_effort`; `top_provider.max_completion_tokens` | no `reasoning` -> `None`; `mandatory` -> `Controllable` without `Off`; `supported_efforts` -> those levels; no list -> `Controllable` with `Off` and the dialect levels | V: live catalog (for example `openai/gpt-5`: mandatory, efforts high / medium / low / minimal) |
| Ollama `/api/show` | `thinking.values` and `thinking.default`; `values: [false]` means no thinking | strings -> those levels; `true` / `false` -> `Off` | V: docs; U: the server version that adds it |
| llama.cpp `/props` | `chat_template_caps.supports_reasoning_effort`; template text with `enable_thinking` or `<think>` | caps true -> `Controllable(Off, Low, Medium, High)`; marker only -> `Controllable(Off)` | V: server source (`caps.h`) |
| vLLM | no direct signal | the OpenRouter name oracle; else `Unknown` | V: docs show no signal |
| Copilot catalog | `capabilities` object | U: field names for effort and output limits | U |
| Venice catalog | `model_spec.capabilities.supportsReasoning`, `.supportsReasoningEffort`, `maxCompletionTokens` | a later resolver | V: live catalog |
| models.dev (later change) | per model: `reasoning` (bool), `reasoning_options` (`[{type: "effort", values: [...]}]`), `limit.context`, `limit.output` | `reasoning: false` -> `None`; effort values -> levels; `limit.output` -> max output | V: live `api.json` |
| Observation | the per-call breakdown counts thinking deltas | the model thinks; levels stay unknown | existing code; used only in the log |

Rules for each surface (`model set`, the TUI, doctor):

- The output limit always works.
- Offered levels = (levels the dialect expresses) ∩ (capability levels).
- `Unknown`: offer `Off` only when a body-field dialect sends it. A Native
  `Off` fails on a hosted model that cannot turn thinking off, so the Native
  dialect offers no level for an unknown model. Show "thinking support unknown
  for this endpoint" with the dialect hint. Netclaw never guesses a level.
- Anthropic: Netclaw sends a level in adaptive mode, so a model offers a level
  other than `Off` only when `capabilities.thinking.types.adaptive.supported`
  is true.
- `model set` and the TUI run the whole validator on each definition that they
  write (the configuration rules, the dialect, and the Ollama `DisableThinking`
  conflict). They check the probed capability only for the field that they
  change. A re-set of the model that the role already uses keeps the role's own
  definition, and `model set` names the other roles of that definition.
- Startup, hot reload, and doctor reject a level other than `Off` on an Ollama
  provider with `DisableThinking: true`.
- `None`: offer no level, with the reason "this model does not think".
- `AlwaysOn`: offer no level, with the reason "this model always thinks; the
  endpoint reports no levels".
- Startup validation checks only the dialect, because the capability is
  resolved at runtime. A provider that rejects a level returns a loud error.

### D6. Self-hosted dialect: set once per provider

`Providers:<name>:VendorOptions:ThinkingDialect` holds `ChatTemplateKwargs`,
`ReasoningEffort`, `DeepSeek`, or `None`. The CLI accepts the kebab-case form
(`chat-template-kwargs`) and persists the enum name.

Resolution order:

1. An explicit `ThinkingDialect` value.
2. A value that `netclaw model set` detected and saved: backend llama.cpp ->
   `ReasoningEffort`.
3. Absent: `Unresolved`. No level is supported. The suppression intent keeps
   its current `chat_template_kwargs` form, so memory curation does not change.

Detection runs only in `model set`, because that command already probes the
endpoint with the model ID. The runtime reads only durable config for the
dialect. When detection fails, `model set --thinking` fails and names the key
and its four values. The output limit always works.

### D7. A level that the provider lacks is rejected (question 3)

Options:

1. Map to the nearest level, and log it once at startup.
2. Reject in `model set`, the TUI, doctor, and startup validation.

Decision: option 2. Option 1 is a silent fallback with a log line: the operator
asks for `medium` and pays for `high`. Today's DeepSeek code does exactly that,
and this change removes it. The error lists the levels that are offered and
the reason for each missing level.

- Positive: provider `deepseek`,
  `netclaw model set main deepseek deepseek-v4 --thinking max` saves `Max`.
- Negative: the same command with `--thinking medium` exits non-zero, prints
  "DeepSeek offers off, low, high, max", and leaves `netclaw.json` unchanged.
- Negative: a hand-edited `Thinking: Low` on a provider with dialect `None`
  fails startup with the same text.

### D8. Callers and precedence (question 5)

The settings attach to the model client, not to a caller. Every caller that
uses a definition gets its values: the session, sub-agents, compaction, the
memory observer, and titles. Each caller uses the definition of its role.

A call-site value wins. The model value fills only an unset field. Reason: the
call site knows the shape of its output. Memory curation sizes its JSON reply
to 4096 tokens and asks for no reasoning.

The suppression intent is a call-site `Off` for each dialect that sends `Off`
in the request body (`DeepSeek`, `ReasoningEffort`, `ChatTemplateKwargs`,
`OllamaThink`, and `Unresolved`). For `Native` and `None`, it sends no thinking
field, as today. A `Native` `Off` would fail on models that cannot turn
thinking off, for example Claude Opus 5.5, and memory curation would break.

### D9. Failover isolation (#2681)

This change fixes #2681. `ModelRequestSettingsChatClient` is the outermost
wrapper of each candidate. It copies the caller's options with
`ChatOptions.Clone()` before any wrapper changes them, and the copy has its own
`AdditionalProperties` dictionary. Thus the vendor-options wrapper and the
dialect change only the copy. `RoutingChatClient` does not change.

Alternative: copy in `RoutingChatClient`. Rejected, because a non-routing
caller of a model client would still share options with retries.

### D10. CLI, TUI, schema, doctor, and visibility

CLI:

- `netclaw model set`: `--max-output-tokens <n>`, `--clear-max-output-tokens`,
  `--thinking <off|low|medium|high|max>`, `--clear-thinking`. The flags follow
  the `--context-window` pattern (ModelCommand.cs:137-185).
- `netclaw model list` gets two columns: "Max out" (the value, or
  `default 32000`) and "Thinking" (the level, or `default`).

TUI model manager (mock-up choices, agreed with the coordinator):

- The role table gets "Max out" and "Thinking" columns.
- `P` opens a properties panel for the model definition of the selected role.
- The panel has an integer field for the output limit (blank means the
  default) and a level picker.
- The picker greys out each level that is not offered, with its reason.
- A definition that several roles share shows "Also used by: ...".
- For an endpoint without a known dialect, only the output field is editable.
  The panel shows the dialect hint.

Validation (one validator before `ModelEntryWriter` writes):

- limit > 0;
- limit < `ContextWindow` when it is configured;
- limit <= the known model max output, when the probe reports one;
- the level is offered (D5);
- no conflict with the Ollama `DisableThinking` vendor option.

Schema (`ModelReference`): `MaxOutputTokens` `{"type": "integer", "minimum":
1}` and `Thinking` `{"type": "string", "enum": ["Off", "Low", "Medium",
"High", "Max"]}`. Both are optional, so no `default` is needed. The provider
entry stays open, so `ThinkingDialect` needs no schema change. Doctor checks it
through the descriptor. Doctor reports a capability problem only when it can
probe the endpoint.

Visibility:

- Startup: `ProviderPluginFactory.Create` logs one Information line for each
  model client. Example:
  `Model client main: provider=vllm model=qwen3 maxOutputTokens=default 32000 (clamped per call; window=131072, modelMax=unknown) thinking=Off (configured) dialect=ReasoningEffort capability=Controllable(Off)`.
- Per call: `ModelRequestSettingsChatClient` logs one Debug line with the
  effective limit and its source (`call`, `configured`, `default`,
  `default-clamped`), the prompt estimate, and the level with its source
  (`call`, `configured`, `suppressed`, `unset`).

Cross-boundary proof: each CLI and TUI test that rejects a value asserts that
the file bytes do not change. Each save test reloads the file through
`ModelConfigurationResolver` and asserts the values that the runtime reads.

## Risks / Trade-offs

- [The 32,000 default raises the cost per call for every provider.] -> The
  release notes and the skill state the change. An explicit value overrides it.
- [The character estimate is low for some tokenizers, so a clamped request can
  still exceed the window.] -> The margin is 5% of the window, at least 1,024
  tokens. The provider error is loud.
- [Thinking tokens count inside the output limit on Anthropic, OpenAI,
  OpenRouter, and Venice. A low limit with a high level fails the turn as
  truncated.] -> The skill states this. The truncation error names the limit.
- [A new hosted model is `Unknown` until a catalog lists it.] -> Only `Off` is
  offered for it. A models.dev resolver later improves coverage.
- [A chat template ignores `enable_thinking` or `reasoning_effort`.] -> The
  server ignores the field with no error. Netclaw cannot see this. The risk
  exists today for memory curation.
- [Compaction and titles inherit `High` or `Max` from a shared definition.] ->
  The operator can give Compaction its own definition.
- [Nothing bounds a single long answer generation inside one model call except
  the output limit. FirstTokenTimeout and NoProgressTimeout reset on each
  token, and the thinking cap and the turn loop guard do not apply. With the
  32,000 default, a model that loops can generate for many minutes (one eval
  call: 32,000 tokens in about 16 minutes, 1 of about 810 calls).] -> Accepted
  by Aaron; no new guard in this change.

## Migration Plan

- No data migration. Both fields are optional.
- Behavior change: each call without an explicit limit now sends the D4
  default. Anthropic calls change from 1024 to up to 32,000. Other providers
  change from no limit to up to 32,000.
- Rollback: revert the code. An older daemon does not know the two fields. Its
  schema check rejects them, and `netclaw doctor --fix` on the older version
  removes them.

## Open Questions

Each item is a check during implementation. A result changes one table row or
one test row, not the approach. The implementation results follow each item.

1. The M.E.AI Responses adapter mapping of `ExtraHigh` (expected `xhigh`, as
   in the Chat adapter).
   Result: verified by decompile of M.E.AI.OpenAI 10.8.3
   (`OpenAIResponsesChatClient`): `ExtraHigh` -> `"xhigh"`.
2. Copilot: the output-limit and effort fields in its model catalog, and
   whether it accepts an effort for non-OpenAI models.
   Result: not verified. Copilot uses the Native dialect. The probe reports no
   Copilot capability, so `model set` offers only `off` for Copilot.
3. How the Anthropic Models API reports that a model rejects
   `thinking.type: disabled` (for example `types.enabled` false).
   Result: not verified. Decision (Aaron): Netclaw offers `off` for each
   Anthropic model that thinks. A model that rejects a disabled thinking
   setting returns a loud provider error. Netclaw never retries with another
   level.
4. ds4: the request fields of `ds4-server`. Its serving guide is not read yet.
   Result: not verified. ds4 is not detected; the operator sets the dialect.
5. SGLang and Ollama `/v1`: `chat_template_kwargs` and `reasoning_effort`
   support.
   Result: not verified. These servers are not detected; the operator sets the
   dialect.
6. vLLM: the exact error when the prompt plus `max_tokens` exceeds
   `max_model_len`, and the gpt-oss level support.
   Result: not verified. The clamp margin covers the case; a provider error
   stays loud.
7. DeepSeek: the output-limit field name in its API reference.
   Result: the field is `max_tokens`. The self-hosted client sends it.

Implementation notes:

- The startup line shows `capability=unresolved` unless the capability is
  already resolved. The client resolves it on first use (D4) and logs one
  Information line with the result.
- "Context window known" uses provenance (D4): a configured `ContextWindow` and
  the window of the endpoint's native probe are known. A catalog window and the
  startup default (32768) are unknown for a self-hosted endpoint. A hosted
  endpoint also counts a catalog window.
- Probe capability sources in this change: llama.cpp `/props` (only when the
  `/v1/models` listing shows llama.cpp markers), the Anthropic model listing,
  and the OpenRouter catalog. The Ollama probe lists `/api/tags` only, so
  `model set` offers only `off` for Ollama until a probe reads `/api/show`.
  The daemon resolver reads `/api/show`.
- No CLI command sets the provider dialect. `model set` detection or an edit of
  `Providers:<name>:VendorOptions:ThinkingDialect` sets it. Doctor and the
  error text name the key and the four values.
- `doctor --fix` keeps an invalid limit or level and reports an error.
