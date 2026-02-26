## MODIFIED Requirements

### Requirement: Multi-provider support

The system SHALL support selecting one provider profile from a supported set.

#### Scenario: Switch provider

- **GIVEN** OpenRouter is configured
- **WHEN** operator selects Anthropic, OpenAI, or Ollama profile
- **THEN** runtime uses selected provider after validation

## ADDED Requirements

### Requirement: Optional live smoke provider checks

The system SHALL support optional provider smoke checks against a local
OpenAI-compatible endpoint such as Ollama.

#### Scenario: Ollama smoke check

- **GIVEN** operator configures Ollama endpoint
- **WHEN** smoke check is invoked explicitly
- **THEN** system reports pass or actionable failure for connectivity/auth

#### Scenario: Local dev default profile

- **GIVEN** local smoke profile is used
- **WHEN** endpoint defaults are applied
- **THEN** provider targets `http://big-gpu:11434`
- **AND** model defaults to `qwen3:30b` with fallback `qwen3:14b`
