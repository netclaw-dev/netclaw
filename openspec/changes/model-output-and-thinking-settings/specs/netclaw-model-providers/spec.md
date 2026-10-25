## ADDED Requirements

### Requirement: Provider-neutral output limit

The system SHALL send the effective output limit to the provider as that
provider's output-limit field. The operator SHALL NOT need to know the field
name. An explicit value from the call site or from the model definition SHALL
be sent as is.

#### Scenario: Limit reaches a self-hosted endpoint

- **GIVEN** a definition on an OpenAI-compatible provider with `MaxOutputTokens` 8000
- **WHEN** a session calls the model
- **THEN** the request body SHALL contain `max_tokens` 8000

#### Scenario: Limit reaches Ollama

- **GIVEN** a definition on an Ollama provider with `MaxOutputTokens` 8000
- **WHEN** a session calls the model
- **THEN** the request SHALL set `num_predict` to 8000

#### Scenario: Explicit limit is not clamped

- **GIVEN** a definition with `MaxOutputTokens` 64000 and a context window of 131072
- **AND** a prompt of about 110000 tokens
- **WHEN** a session calls the model
- **THEN** the request SHALL carry 64000

### Requirement: Default output limit with a per-call clamp

When neither the call site nor the definition sets an output limit, the system
SHALL send a default to every provider. The default SHALL be the smallest of
32000, the model's known maximum output, and the context window minus the
estimated prompt tokens minus a margin. The margin SHALL be the larger of 1024
and 5% of the context window. The clamped value SHALL NOT be below 1024. An
unknown input SHALL NOT limit the value. The system SHALL compute the default
for each call.

#### Scenario: Short prompt gets the full default

- **GIVEN** a definition without `MaxOutputTokens`, a context window of 131072, and an unknown model maximum
- **WHEN** a session calls the model with a prompt of about 5000 tokens
- **THEN** the request SHALL carry 32000 with source `default`

#### Scenario: Prompt near the window clamps the default

- **GIVEN** a definition without `MaxOutputTokens` and a context window of 131072
- **WHEN** a session calls the model with a prompt of about 110000 tokens
- **THEN** the request SHALL carry 14519 with source `default-clamped`

#### Scenario: Prompt past the floor sends the floor and warns

- **GIVEN** a definition without `MaxOutputTokens` and a context window of 32768
- **WHEN** a session calls the model with a prompt of about 31000 tokens
- **THEN** the request SHALL carry 1024 with source `default-clamped`
- **AND** the system SHALL log a warning

#### Scenario: Unknown context window does not clamp

- **GIVEN** a definition without `MaxOutputTokens` whose context window is unknown
- **WHEN** a session calls the model
- **THEN** the request SHALL carry 32000 with source `default`

#### Scenario: Known model maximum below the default

- **GIVEN** a definition without `MaxOutputTokens` whose model reports a maximum output of 16384
- **WHEN** a session calls the model with a short prompt
- **THEN** the request SHALL carry 16384 with source `default-clamped`

#### Scenario: Anthropic no longer uses the SDK default

- **GIVEN** an Anthropic definition without `MaxOutputTokens`
- **WHEN** a session calls the model with a short prompt
- **THEN** the request SHALL NOT carry `max_tokens` 1024

### Requirement: Provider-neutral thinking level through the thinking dialect

Each provider SHALL resolve exactly one thinking dialect for each provider
entry. The dialect SHALL translate the effective thinking level into the
provider's own request fields. The dialect SHALL also translate the reasoning
suppression intent of a call site. The system SHALL use one dialect mechanism
for both purposes. When the effective level is unset, the system SHALL send no
thinking field.

#### Scenario: OpenAI effort comes from the neutral level

- **GIVEN** a definition on an OpenAI provider with `Thinking` `Low`
- **WHEN** a session calls the model
- **THEN** the request SHALL set the reasoning effort to `low`

#### Scenario: Anthropic Max sends the max effort

- **GIVEN** an Anthropic definition with `Thinking` `Max`
- **WHEN** a session calls the model
- **THEN** the request SHALL contain adaptive thinking and `output_config.effort` `max`

#### Scenario: DeepSeek level maps to the DeepSeek fields

- **GIVEN** a definition on a DeepSeek provider with `Thinking` `Max`
- **WHEN** a session calls the model
- **THEN** the request body SHALL contain `thinking.type` `enabled` and `reasoning_effort` `max`

#### Scenario: Off on a reasoning-effort backend

- **GIVEN** an OpenAI-compatible provider with dialect `ReasoningEffort`
- **AND** a definition on that provider with `Thinking` `Off`
- **WHEN** a session calls the model
- **THEN** the request body SHALL contain `reasoning_effort` `none`

#### Scenario: OpenRouter keeps exclude with the effort

- **GIVEN** an OpenRouter provider with `ExcludeReasoning` true
- **AND** a definition on that provider with `Thinking` `High`
- **WHEN** a session calls the model
- **THEN** the request body SHALL contain `reasoning.effort` `high` and `reasoning.exclude` true

#### Scenario: Unset level sends nothing

- **GIVEN** a definition on an Anthropic provider without `Thinking`
- **WHEN** a session calls the model
- **THEN** the request SHALL contain no thinking field and no effort field

### Requirement: Thinking capability of a model

The capability resolvers SHALL report a thinking capability for each model:
`Unknown`, `None`, `AlwaysOn`, or `Controllable` with a set of levels. They
SHALL also report the model's maximum output when a source gives it. A
provider-native probe SHALL win over a catalog, and a catalog SHALL win over
`Unknown`. The system SHALL offer a thinking level only when the dialect can
express it and the capability includes it. When the capability is `Unknown`,
the system SHALL offer only `Off`, and only when the dialect can express `Off`.
The system SHALL NOT guess a level.

#### Scenario: Catalog levels are offered

- **GIVEN** an OpenRouter model whose catalog entry lists the efforts low, medium, and high, with `mandatory` true
- **WHEN** the operator opens the thinking picker for that model
- **THEN** the picker SHALL offer `Low`, `Medium`, and `High`
- **AND** it SHALL grey out `Off` with the reason that the model always thinks

#### Scenario: Native probe wins over the catalog

- **GIVEN** the Anthropic Models API reports a model's supported efforts
- **AND** the OpenRouter catalog reports different values for the same model
- **WHEN** the capabilities are resolved
- **THEN** the Anthropic values SHALL apply

#### Scenario: Unknown capability offers only Off

- **GIVEN** a vLLM provider with dialect `ReasoningEffort` and a model that no source knows
- **WHEN** the operator runs `model set` with `--thinking low`
- **THEN** the command SHALL fail with "thinking support unknown for this endpoint"
- **AND** `netclaw.json` SHALL stay unchanged
- **AND** `--thinking off` on the same model SHALL be saved

#### Scenario: Model without thinking offers no level

- **GIVEN** a catalog entry without reasoning support
- **WHEN** the operator sets `--thinking off`
- **THEN** the command SHALL fail with the reason that the model does not think

### Requirement: Unsupported thinking level is rejected

The system SHALL reject a thinking level that is not offered. Rejection SHALL
occur in `netclaw model set`, in the TUI model manager, and in `netclaw doctor`.
Startup validation SHALL reject a level that the provider's dialect cannot
express. The system SHALL NOT map a level to a different level. The error SHALL
name the provider, the offered levels, and the reason for each missing level.
The output limit SHALL stay usable when no thinking level is offered.

#### Scenario: Offered level is accepted

- **GIVEN** provider `deepseek` uses the DeepSeek dialect
- **WHEN** the operator runs `netclaw model set main deepseek deepseek-v4 --thinking max`
- **THEN** the command SHALL save `Thinking` `Max`

#### Scenario: Missing level is rejected before persistence

- **GIVEN** provider `deepseek` uses the DeepSeek dialect, which offers `Off`, `Low`, `High`, and `Max`
- **WHEN** the operator runs `netclaw model set main deepseek deepseek-v4 --thinking medium`
- **THEN** the command SHALL exit with a non-zero code
- **AND** the error SHALL list `off`, `low`, `high`, and `max`
- **AND** `netclaw.json` SHALL stay unchanged

#### Scenario: Hand-edited unsupported level fails startup

- **GIVEN** `netclaw.json` holds `Thinking` `Low` for a model of a provider with dialect `None`
- **WHEN** the daemon starts
- **THEN** startup validation SHALL fail with the same remediation that doctor shows

#### Scenario: Conflict with the Ollama provider switch

- **GIVEN** an Ollama provider with vendor option `DisableThinking` true
- **WHEN** a definition on that provider has a `Thinking` value other than `Off`
- **THEN** doctor and `model set` SHALL report the conflict and SHALL NOT save the value

### Requirement: Self-hosted thinking dialect is set once per provider

An OpenAI-compatible provider SHALL get its thinking dialect from
`Providers:<name>:VendorOptions:ThinkingDialect`, with the values
`ChatTemplateKwargs`, `ReasoningEffort`, `DeepSeek`, and `None`. When the value
is absent, the provider SHALL support no thinking level. The reasoning
suppression intent SHALL then keep its existing `chat_template_kwargs` form.
`netclaw model set` SHALL detect the dialect where the endpoint shows it, and
it SHALL persist the detected value in the same atomic write. An invalid value
SHALL fail doctor and startup.

#### Scenario: Detected dialect is saved once

- **GIVEN** a llama.cpp provider without `ThinkingDialect`
- **WHEN** the operator runs `netclaw model set main llama qwen3 --thinking off`
- **THEN** the command SHALL save `ThinkingDialect` `ReasoningEffort` on the provider
- **AND** it SHALL save `Thinking` `Off` on the definition in the same write
- **AND** it SHALL print the detected dialect

#### Scenario: Undetectable dialect asks for one setting

- **GIVEN** a vLLM provider without `ThinkingDialect`
- **WHEN** the operator sets `--thinking off` on a model of that provider
- **THEN** the command SHALL fail without a write
- **AND** the error SHALL name the `ThinkingDialect` key and its four values

#### Scenario: Output limit works without a dialect

- **GIVEN** a vLLM provider without `ThinkingDialect`
- **WHEN** the operator sets `--max-output-tokens 16000` on a model of that provider
- **THEN** the command SHALL save the limit

### Requirement: Call-site values take precedence over model settings

A value that a call site sets on a request SHALL win over the model
definition's value and over the default. The model value SHALL fill only a
field that the call site left unset. The reasoning suppression intent SHALL
count as a call-site thinking level of `Off` for each dialect that can send
`Off` in the request body. For any other dialect, the suppression intent SHALL
send no thinking field, as it does today. A call that has the suppression
intent SHALL NOT fail because the dialect lacks `Off`.

#### Scenario: Curation keeps its own limit

- **GIVEN** the memory curation call sets an output limit of 4096
- **AND** the curation model definition has `MaxOutputTokens` 32000
- **WHEN** curation calls the model
- **THEN** the request SHALL carry 4096

#### Scenario: Suppression wins over the model level

- **GIVEN** a DeepSeek definition with `Thinking` `High`
- **AND** a call site sets the reasoning suppression intent
- **WHEN** the call reaches the provider
- **THEN** the request body SHALL contain `thinking.type` `disabled`
- **AND** it SHALL contain no `reasoning_effort` field

#### Scenario: Session call inherits the model values

- **GIVEN** the Main definition has `MaxOutputTokens` 32000 and `Thinking` `Low`
- **WHEN** a session turn calls the model without its own values
- **THEN** the provider request SHALL carry the mapped fields for 32000 and `Low`

### Requirement: Failover attempts do not share changed request options

Each candidate in a role SHALL receive its own copy of the caller's request
options, and the copy SHALL include a separate copy of the additional
properties. A candidate's mapping SHALL NOT change the options that the next
candidate receives. Each candidate SHALL apply its own definition's settings.

#### Scenario: Fallback receives none of the first provider's fields

- **GIVEN** Main is an Ollama definition with `Thinking` `Off`
- **AND** Fallback is a DeepSeek definition with `Thinking` `High`
- **WHEN** the Ollama call adds `think` and then fails
- **THEN** the DeepSeek request SHALL contain no `think` field
- **AND** it SHALL contain the DeepSeek fields for `High`

#### Scenario: Vendor options do not leak to the fallback

- **GIVEN** the first candidate has a vendor option that adds a request field
- **WHEN** the first candidate fails and the fallback runs
- **THEN** the fallback request SHALL NOT contain that field

### Requirement: Effective output limit and thinking level are visible

At startup, the daemon SHALL log one line for each model client. The line SHALL
show the configured limit or the default with its known inputs (context window
and model maximum), the thinking level with its source, the thinking dialect,
and the thinking capability. At debug level, each model call SHALL log the
effective limit with its source (`call`, `configured`, `default`, or
`default-clamped`), the prompt estimate, and the level with its source
(`call`, `configured`, `suppressed`, or `unset`).

#### Scenario: Startup line shows the values

- **GIVEN** Main has `Thinking` `Low` on an OpenAI provider and no `MaxOutputTokens`
- **WHEN** the daemon starts
- **THEN** the log SHALL contain one line with the default limit, `thinking=Low (configured)`, the dialect, and the capability

#### Scenario: Debug line shows the clamp

- **GIVEN** debug logs are on
- **WHEN** a session call clamps the default
- **THEN** the debug line SHALL show the sent value with source `default-clamped` and the prompt estimate
