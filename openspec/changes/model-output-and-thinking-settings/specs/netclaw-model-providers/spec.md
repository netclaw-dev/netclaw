## ADDED Requirements

### Requirement: Provider-neutral output limit

The system SHALL send a model definition's `MaxOutputTokens` to the provider as
that provider's output-limit field. The operator SHALL NOT need to know the
field name. When the effective limit is unset, the system SHALL send no limit,
except where the "Anthropic output limit when unset" requirement applies.

#### Scenario: Limit reaches a self-hosted endpoint

- **GIVEN** a definition on an OpenAI-compatible provider with `MaxOutputTokens` 8000
- **WHEN** a session calls the model
- **THEN** the request body SHALL contain `max_tokens` 8000

#### Scenario: Limit reaches Ollama

- **GIVEN** a definition on an Ollama provider with `MaxOutputTokens` 8000
- **WHEN** a session calls the model
- **THEN** the request SHALL set `num_predict` to 8000

#### Scenario: Unset limit sends nothing

- **GIVEN** a definition on an OpenAI provider without `MaxOutputTokens`
- **WHEN** a session calls the model
- **THEN** the request SHALL contain no output-limit field

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

#### Scenario: DeepSeek level maps to the DeepSeek fields

- **GIVEN** a definition on a DeepSeek provider with `Thinking` `Max`
- **WHEN** a session calls the model
- **THEN** the request body SHALL contain `thinking.type` `enabled` and `reasoning_effort` `max`

#### Scenario: Off on a chat-template backend

- **GIVEN** an OpenAI-compatible provider with dialect `ChatTemplateKwargs`
- **AND** a definition on that provider with `Thinking` `Off`
- **WHEN** a session calls the model
- **THEN** the request body SHALL contain `chat_template_kwargs.enable_thinking` `false`

#### Scenario: Unset level sends nothing

- **GIVEN** a definition on an Anthropic provider without `Thinking`
- **WHEN** a session calls the model
- **THEN** the request SHALL contain no thinking field and no effort field

### Requirement: Unsupported thinking level is rejected

The system SHALL reject a thinking level that the provider's resolved dialect
cannot send. Rejection SHALL occur in `netclaw model set`, in the TUI model
manager, in `netclaw doctor`, and in startup validation. The system SHALL NOT
map a level to a different level. The error SHALL name the provider, the
dialect, and the levels that the dialect supports. The output limit SHALL stay
usable when the thinking level is unsupported.

#### Scenario: Supported level is accepted

- **GIVEN** provider `deepseek` uses the DeepSeek dialect
- **WHEN** the operator runs `netclaw model set main deepseek deepseek-v4 --thinking max`
- **THEN** the command SHALL save `Thinking` `Max`

#### Scenario: Missing level is rejected before persistence

- **GIVEN** provider `deepseek` uses the DeepSeek dialect, which supports `Off`, `High`, and `Max`
- **WHEN** the operator runs `netclaw model set main deepseek deepseek-v4 --thinking low`
- **THEN** the command SHALL exit with a non-zero code
- **AND** the error SHALL list `off`, `high`, and `max`
- **AND** `netclaw.json` SHALL stay unchanged

#### Scenario: On/off backend rejects a graded level

- **GIVEN** an OpenAI-compatible provider with dialect `ChatTemplateKwargs`
- **WHEN** the operator sets `--thinking medium` on a model of that provider
- **THEN** the command SHALL fail and SHALL say that only `off` is supported
- **AND** the error SHALL say that an unset level uses the model's default

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
- **AND** the chat template from the endpoint reads `enable_thinking`
- **WHEN** the operator runs `netclaw model set main llama qwen3 --thinking off`
- **THEN** the command SHALL save `ThinkingDialect` `ChatTemplateKwargs` on the provider
- **AND** it SHALL save `Thinking` `Off` on the definition in the same write
- **AND** it SHALL print the detected dialect

#### Scenario: Undetectable dialect asks for one setting

- **GIVEN** a vLLM provider without `ThinkingDialect`, whose endpoint does not show its chat template
- **WHEN** the operator sets `--thinking off` on a model of that provider
- **THEN** the command SHALL fail without a write
- **AND** the error SHALL name the `ThinkingDialect` key and its four values

#### Scenario: Output limit works without a dialect

- **GIVEN** a vLLM provider without `ThinkingDialect`
- **WHEN** the operator sets `--max-output-tokens 16000` on a model of that provider
- **THEN** the command SHALL save the limit

### Requirement: Call-site values take precedence over model settings

A value that a call site sets on a request SHALL win over the model
definition's value. The model value SHALL fill only a field that the call site
left unset. The reasoning suppression intent SHALL count as a call-site thinking
level of `Off` for each dialect that can send `Off` in the request body. For
any other dialect, the suppression intent SHALL send no thinking field, as it
does today. A call that has the suppression intent SHALL NOT fail because the
dialect lacks `Off`.

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

### Requirement: Anthropic output limit when unset

When the effective output limit for an Anthropic call is unset, the Anthropic
provider SHALL send a documented Netclaw default of 32000 tokens. It SHALL NOT
send the SDK default of 1024. The startup line and the debug line SHALL show the
source of this value as the provider default.

#### Scenario: Unset Anthropic limit uses the documented default

- **GIVEN** an Anthropic definition without `MaxOutputTokens`
- **WHEN** a session calls the model
- **THEN** the request SHALL set `max_tokens` to 32000

#### Scenario: Configured Anthropic limit wins

- **GIVEN** an Anthropic definition with `MaxOutputTokens` 64000
- **WHEN** a session calls the model
- **THEN** the request SHALL set `max_tokens` to 64000

### Requirement: Effective output limit and thinking level are visible

At startup, the daemon SHALL log one line for each model client. The line SHALL
show the effective output limit and its source (`model`, `provider-default`, or
`unset`), the thinking level, and the thinking dialect. At debug level, each
model call SHALL log the effective limit and level with their sources (`call`,
`model`, `provider-default`, `suppressed`, or `unset`).

#### Scenario: Startup line shows the values

- **GIVEN** Main has `MaxOutputTokens` 32000 and `Thinking` `Low` on an OpenAI provider
- **WHEN** the daemon starts
- **THEN** the log SHALL contain one line with `maxOutputTokens=32000 (model)` and `thinking=Low (model)` and the dialect

#### Scenario: Debug line shows the call-site source

- **GIVEN** debug logs are on
- **WHEN** memory curation calls a model with its own limit
- **THEN** the debug line SHALL show `maxOutputTokens=4096 (call)`
