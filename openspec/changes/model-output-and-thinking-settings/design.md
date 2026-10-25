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
- `netclaw model set` already probes the provider (`IProviderProbe`), and
  `OpenAiCompatibleCapabilityResolver` already tells ds4, vLLM, llama.cpp, and
  generic servers apart. Only llama.cpp shows its chat template (`/props`).
- Anthropic SDK 12.50.0 sends `max_tokens` 1024 when `MaxOutputTokens` is
  unset. Current Claude models use adaptive thinking with
  `output_config.effort` (`low`, `medium`, `high`, `xhigh`, `max`). Some
  models reject `thinking: {type: "disabled"}`.

## Goals / Non-Goals

**Goals:**

- One mechanism maps the two neutral settings to each provider. It extends the
  existing dialect, and it does not add a parallel one.
- The CLI, the TUI, doctor, and startup use one validator.
- A failover candidate never sees another candidate's request fields.

**Non-Goals:**

- No per-role or per-call-site settings. No thinking token budget.
- No per-model table of the levels that a hosted model accepts. A hosted
  provider that rejects a level returns its own error.
- No reserve for the output limit in the session context budget.
- No TUI provider-editor field for `ThinkingDialect`. The CLI and
  `netclaw.json` set it.

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
  a model that cannot accept it. It also splits model metadata across two
  places.
- Alternative: add an `On` level. Rejected. An unset level already means "the
  model's default". On/off backends need only `Off`.

### D2. One mechanism: the thinking dialect

Rename `ReasoningSuppressionDialect` to `ThinkingDialect` and extend it. The
dialect translates both the effective thinking level and the suppression
intent.

| Dialect | Used by | Off | Low | Medium | High | Max |
|---|---|---|---|---|---|---|
| `Native` | Anthropic, OpenAI, Codex, Copilot, OpenRouter, Venice | yes | yes | yes | yes | yes |
| `DeepSeek` | DeepSeek; OpenAI-compatible when set | yes | no | no | yes | yes |
| `ReasoningEffort` | OpenAI-compatible when set | no | yes | yes | yes | no |
| `ChatTemplateKwargs` | OpenAI-compatible when set or detected | yes | no | no | no | no |
| `OllamaThink` | Ollama | yes | no | no | no | no |
| `None` | OpenAI-compatible when set | no | no | no | no | no |
| `Unresolved` | OpenAI-compatible with no value | no | no | no | no | no |

`Unresolved` is not a config value. It keeps today's suppression form
(`chat_template_kwargs`) and supports no level (D5). `Native` replaces today's
`None` for strict SDKs. It sets the standard
`ChatOptions.Reasoning`, and the SDK adapter writes the wire field. The other
dialects write request-body fields through `AdditionalProperties`, as today.

The dialect resolution moves from `ILlmProviderPlugin` to `IProviderDescriptor`
as `ResolveThinkingDialect(ProviderEntry)`. The CLI and doctor have
descriptors, so they use the same resolution as the daemon. The table above is
one static map in `Netclaw.Providers`.

`ReasoningSuppressionChatClient` becomes `ModelRequestSettingsChatClient`. It
is the outermost wrapper of each model client, and the factory gives it the
`ModelReference` and the dialect. The DeepSeek reasoning code moves out of
`OpenAiCompatibleChatClient` into the DeepSeek dialect. The DeepSeek wire
profile keeps only its message replay rules.

Per-call flow (schematic; omits the logging scope and cancellation):

```
ModelRequestSettingsChatClient.Call(messages, callerOptions):
  o = callerOptions?.Clone() ?? new ChatOptions()    // D8: own copy per candidate
  (limit, limitSource) =
      o.MaxOutputTokens        -> (it, call)
      model.MaxOutputTokens    -> (it, model)
      descriptor.DefaultMaxOutputTokens -> (it, provider-default)   // Anthropic only
      otherwise                -> (null, unset)
  o.MaxOutputTokens = limit
  suppress = o.AdditionalProperties.Remove(SuppressReasoning)       // always removed
  (level, levelSource) =
      suppress and dialect sends Off in the body -> (Off, suppressed)
      suppress (Native, ReasoningEffort, None)   -> (null, suppressed)  // today's behavior
      // Unresolved: suppress -> chat_template_kwargs, as today
      o.Reasoning is set       -> (from o.Reasoning, call)
      model.Thinking           -> (it, model)
      otherwise                -> (null, unset)
  dialect.Apply(o, level)     // throws when the level is unsupported (D4)
  log.Debug(limit, limitSource, level, levelSource, dialect)
  return inner.Call(messages, o)   // inner = VendorOptions wrapper -> SDK client
```

Ownership and data:

| Decision | Owner | Data |
|---|---|---|
| Limit and level of a model | operator, via `ModelEntryWriter` | durable (`Models:Definitions`) |
| Dialect of a provider | provider descriptor | durable (`VendorOptions`) or code constant |
| Supported levels of a dialect | static dialect map | code constant |
| Dialect detection | `netclaw model set`, with the existing probe | call-local, then durable |
| Validation | one validator for CLI, TUI, doctor, startup | call-local |
| Effective values and wire fields | `ModelRequestSettingsChatClient` | call-local (copied options) |
| Anthropic default limit | Anthropic descriptor | code constant |
| OpenRouter effort and exclude merge | `OpenRouterReasoningExcludePolicy` | call-local |

Alternative: a new `IThinkingMapper` per plugin. Rejected, because the dialect
already is that seam, and two seams would drift.

### D3. Per-provider mapping

Prefer the standard `ChatOptions.MaxOutputTokens` and `ChatOptions.Reasoning`
where the SDK maps them. Use a dialect body field only where it does not.
"OQ" marks a row that needs a check (see Open Questions).

| Provider (wire) | Output limit field | Off | Low / Medium / High | Max | Owner |
|---|---|---|---|---|---|
| Anthropic (Messages, SDK adapter) | `max_tokens` (32000 when unset, D6) | `thinking.type: disabled` | adaptive + `output_config.effort` low / medium / high | adaptive + effort `max` | Native; plugin-owned request if the adapter differs (OQ1) |
| OpenAI API key and Codex (Responses) | `max_output_tokens` | `reasoning.effort: none` | `low` / `medium` / `high` | `xhigh` | Native |
| GitHub Copilot (Responses or Chat) | `max_output_tokens` or `max_completion_tokens` (OQ3) | effort `none` | `low` / `medium` / `high` | `xhigh` | Native (OQ4) |
| OpenRouter (Chat + policy) | `max_completion_tokens` (OQ3) | `reasoning.effort: none` (OQ5) | `reasoning.effort` low / medium / high, with `exclude` kept | `reasoning.effort: xhigh` (OQ5) | Native + the policy merges, not replaces |
| Venice (Chat) | `max_completion_tokens` (OQ3) | `reasoning_effort: none` (OQ6) | `reasoning_effort` low / medium / high (OQ6) | `xhigh` (OQ6) | Native |
| DeepSeek (self-hosted client) | `max_tokens` | `thinking.type: disabled` | rejected / rejected / `thinking.type: enabled` + `reasoning_effort: high` | `enabled` + `reasoning_effort: max` | `DeepSeek` dialect |
| Ollama (OllamaSharp) | `num_predict` | `think: false` | rejected (OQ7) | rejected | `OllamaThink` dialect |
| OpenAI-compatible (self-hosted client) | `max_tokens` | per backend rows below | per backend rows below | per backend rows below | dialect from `VendorOptions` |

Native rows map `Off -> ReasoningEffort.None`, `Low -> Low`, `Medium -> Medium`,
`High -> High`, `Max -> ExtraHigh`. The Anthropic plugin owns its mapping where
`ExtraHigh` must become `max`, not `xhigh`.

OpenAI-compatible per-backend rows. The output limit uses `max_tokens` on every
row. vLLM, llama.cpp, SGLang, Ollama's `/v1`, and DeepSeek accept `max_tokens`.
None of these targets needs `max_completion_tokens` (OQ8 confirms llama.cpp and
SGLang).

| Backend + model template | Dialect source | Off | Low / Medium / High | Max |
|---|---|---|---|---|
| llama.cpp, template reads `enable_thinking` (Qwen3, GLM) | detected from `/props` | `chat_template_kwargs.enable_thinking: false` | rejected | rejected |
| llama.cpp, template reads `reasoning_effort` (gpt-oss) | operator (OQ9) | rejected | OQ9 | rejected |
| llama.cpp server option `--reasoning-budget` | server-wide | not per request; Netclaw does not set it | n/a | n/a |
| vLLM or SGLang, Qwen3-style template | operator: `ChatTemplateKwargs` | `chat_template_kwargs.enable_thinking: false` | rejected | rejected |
| vLLM, template that reads `reasoning_effort` (gpt-oss) | operator: `ReasoningEffort` | rejected | `reasoning_effort` low / medium / high | rejected |
| ds4 (DeepSeek V4 server) | detected from `owned_by: ds4.c` (OQ10) | DeepSeek fields | rejected / rejected / high | max |
| Ollama `/v1` OpenAI API | operator: `ReasoningEffort` (OQ11) | rejected | `reasoning_effort` | rejected |
| Generic proxy (for example LiteLLM) | operator: `ReasoningEffort` or `None` | per dialect | per dialect | per dialect |

A `chat_template_kwargs` value from the caller is merged, not replaced. The
dialect writes a new dictionary, so the caller's object does not change.

### D4. A level that the dialect lacks is rejected (question 3)

Options:

1. Map to the nearest level, and log it once at startup.
2. Reject in `model set`, the TUI, doctor, and startup validation.

Decision: option 2. Option 1 is a silent fallback with a log line: the operator
asks for `low` and pays for `high`. Today's DeepSeek code does exactly that,
and this change removes it. The error lists the levels that the dialect
supports and says that an unset level uses the model's default.

- Positive: provider `deepseek` (DeepSeek dialect),
  `netclaw model set main deepseek deepseek-v4 --thinking max` saves `Max`.
- Negative: the same command with `--thinking low` exits non-zero, prints
  "DeepSeek supports off, high, max", and leaves `netclaw.json` unchanged.
- Negative: a hand-edited `Thinking: Low` on a provider with dialect `None`
  fails startup with the same text.

The `Native` dialect accepts every level. A hosted model can still reject a
level, for example `Off` on Claude Opus 5.5. That call fails loudly with the
provider's error. Netclaw does not keep a per-model table.

### D5. Self-hosted dialect: set once per provider

`Providers:<name>:VendorOptions:ThinkingDialect` holds `ChatTemplateKwargs`,
`ReasoningEffort`, `DeepSeek`, or `None`. The CLI accepts the kebab-case form
(`chat-template-kwargs`) and persists the enum name.

Resolution order:

1. An explicit `ThinkingDialect` value.
2. A value that `netclaw model set` detected and saved: `/props` chat template
   reads `enable_thinking` -> `ChatTemplateKwargs`; backend `ds4` -> `DeepSeek`.
3. Absent: no thinking level is supported. The suppression intent keeps its
   current `chat_template_kwargs` form, so memory curation does not change.

Detection runs only in `model set`, because that command already probes the
endpoint with the model ID. The runtime reads only durable config, so a slow or
failed probe cannot change a request at runtime. When detection fails,
`model set --thinking` fails and names the key and its four values. The output
limit always works.

Alternative: detect at daemon startup. Rejected, because capability detection
is asynchronous and the client stack is built before it ends.

### D6. Unset behavior and the Anthropic default (question 4)

When both the call site and the definition leave a value unset, Netclaw sends
nothing. Today's behavior stays for every provider except Anthropic.

The Anthropic SDK sends `max_tokens` 1024 when the limit is unset. Thinking
tokens count inside that limit. Since `fail-truncated-reply-and-cap-thinking`,
a reply that hits the limit fails the turn. Options:

| Option | Effect |
|---|---|
| A. Keep 1024 | Long Anthropic replies fail as truncated. No cost change. |
| B. Netclaw default 32000, documented and logged | Long replies work. Each call can cost more. Every Claude 4.x and later model accepts 32000. |
| C. Require `MaxOutputTokens` on Anthropic definitions | Breaks the "existing deployment starts after upgrade" requirement. |
| D. Read the model's own maximum from the Anthropic Models API | Exact, but adds detection plumbing. |

Recommendation: B. The Anthropic descriptor declares `DefaultMaxOutputTokens`
32000. The startup line and the debug line show `provider-default`, so the
value is not silent. D can replace the constant later. **This is an open
decision for Aaron.** If he picks A, delete the "Anthropic output limit when
unset" requirement and the descriptor constant. Nothing else changes.

### D7. Callers and precedence (question 5)

The settings attach to the model client, not to a caller. Every caller that
uses a definition gets its values: the session, sub-agents, compaction, the
memory observer, and titles. Each caller uses the definition of its role.

A call-site value wins. The model value fills only an unset field. Reason: the
call site knows the shape of its output. Memory curation sizes its JSON reply
to 4096 tokens and asks for no reasoning. A model default of 32000 or `High`
must not change that.

The suppression intent is a call-site `Off` for each dialect that sends `Off`
in the request body (`DeepSeek`, `ChatTemplateKwargs`, `OllamaThink`, and
`Unresolved`). For `Native`, `ReasoningEffort`, and `None`, it sends no thinking
field, as today. A `Native` `Off` would fail on
models that cannot turn thinking off, for example Claude Opus 5.5, and memory
curation would break.

### D8. Failover isolation (#2681)

This change fixes #2681. `ModelRequestSettingsChatClient` is the outermost
wrapper of each candidate. It copies the caller's options with
`ChatOptions.Clone()` before any wrapper changes them, and the copy has its own
`AdditionalProperties` dictionary. Thus the vendor-options wrapper and the
dialect change only the copy. `RoutingChatClient` does not change.

Alternative: copy in `RoutingChatClient`. Rejected, because a non-routing
caller of a model client would still share options with retries. The copy
belongs with the code that changes the options.

### D9. Visibility

- Startup: `ProviderPluginFactory.Create` logs one Information line for each
  model client. Example:
  `Model client main: provider=anthropic model=claude-opus-5-5 maxOutputTokens=32000 (provider-default) thinking=Low (model) dialect=Native`.
- Per call: `ModelRequestSettingsChatClient` logs one Debug line with the
  effective values and their sources (`call`, `model`, `provider-default`,
  `suppressed`, `unset`).
- The existing capability line in `ModelCapabilityActor` does not change. It
  does not know the dialect or the provider default.

### D10. CLI, TUI, schema, and doctor

- `netclaw model set`: `--max-output-tokens <n>`, `--clear-max-output-tokens`,
  `--thinking <off|low|medium|high|max>`, `--clear-thinking`. The flags follow
  the `--context-window` pattern (ModelCommand.cs:137-185).
- The TUI model manager: an integer field for the output limit (blank means
  unset) and a picker for the level (unset, off, low, medium, high, max).
- Both call one validator before `ModelEntryWriter` writes:
  - limit > 0;
  - limit < `ContextWindow` when it is configured;
  - level supported by the provider's dialect;
  - no conflict with the Ollama `DisableThinking` vendor option.
- Schema (`ModelReference`): `MaxOutputTokens` `{"type": "integer",
  "minimum": 1}` and `Thinking` `{"type": "string", "enum": ["Off", "Low",
  "Medium", "High", "Max"]}`. Both are optional, so no `default` is needed.
  The provider entry stays open, so `ThinkingDialect` needs no schema change.
  Doctor checks it through the descriptor.
- Doctor reports each rule above as an error with the key path. When the
  detected context window is not above the limit, startup logs a warning.
- Cross-boundary proof: each CLI and TUI test that rejects a value asserts that
  the file bytes do not change. Each save test reloads the file through
  `ModelConfigurationResolver` and asserts the values that the runtime reads.

## Risks / Trade-offs

- [Thinking tokens count inside the output limit on Anthropic and OpenAI. A low
  limit with a high level fails the turn as truncated.] -> The runbook and the
  `netclaw-operations` skill state this. The truncation error names the limit.
- [A hosted model rejects a level that `Native` accepts.] -> The call fails
  with the provider's error. Doctor cannot know. The skill lists the known
  cases.
- [A chat template ignores `enable_thinking`.] -> The server ignores the field
  with no error. Netclaw cannot see this. This risk exists today for memory
  curation.
- [Compaction and titles inherit `High` or `Max` from a shared definition.] ->
  The operator can give Compaction its own definition.
- [B raises the Anthropic cost per call.] -> The default appears on the startup
  line and in the release notes.

## Migration Plan

- No data migration. Both fields are optional, and an absent value keeps
  today's request shape.
- One behavior change: an Anthropic call without a configured limit sends 32000
  in place of 1024 (if Aaron accepts D6 option B).
- Rollback: revert the code. An older daemon does not know the two fields. Its
  schema check rejects them, and `netclaw doctor --fix` on the older version
  removes them.

## Open Questions

Each item is a check during implementation. A result changes one table row or
one test row, not the approach.

1. OQ1: Does the Anthropic SDK 12.50.0 adapter map `ChatOptions.Reasoning` to
   adaptive thinking plus `output_config.effort`, or to `budget_tokens`? How
   does it map `ExtraHigh`? If it does not send adaptive plus `max`, the
   Anthropic plugin builds the thinking fields itself. Check with ILSpy.
2. OQ2: The OpenAI Responses adapter maps `ExtraHigh` to `xhigh` and `None` to
   `none`. Confirm in the M.E.AI OpenAI adapter.
3. OQ3: The OpenAI SDK Chat adapter sends `max_completion_tokens`, not
   `max_tokens`. Do OpenRouter, Venice, and Copilot Chat accept it?
4. OQ4: Does Copilot honor the reasoning effort for non-OpenAI models?
5. OQ5: Which OpenRouter `reasoning.effort` values exist (`none`, `xhigh`)?
   Does OpenRouter read a top-level `reasoning_effort`, or does the policy have
   to move it into `reasoning`?
6. OQ6: Does Venice accept `reasoning_effort`, or only
   `venice_parameters.disable_thinking`?
7. OQ7: Does OllamaSharp 5.4.30 send a string `think` (`low`, `medium`,
   `high`) for gpt-oss? If yes, `OllamaThink` gains those three levels.
8. OQ8: Confirm that llama.cpp and SGLang accept `max_tokens` (expected yes).
9. OQ9: Does llama.cpp read gpt-oss effort from `chat_template_kwargs.reasoning_effort`
   or from a top-level `reasoning_effort`?
10. OQ10: Does the ds4.c server accept the DeepSeek `thinking` field, and does
    it need the DeepSeek replay rules?
11. OQ11: Does Ollama's `/v1` endpoint accept `reasoning_effort`?
