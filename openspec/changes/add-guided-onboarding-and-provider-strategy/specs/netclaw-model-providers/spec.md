## ADDED Requirements

### Requirement: OpenRouter default provider

The system SHALL default to OpenRouter during first-run setup.

#### Scenario: Default provider selection

- **WHEN** operator accepts defaults in onboarding
- **THEN** provider is configured as OpenRouter

### Requirement: Multi-provider support

The system SHALL support selecting one provider profile from a supported set.

#### Scenario: Switch provider

- **GIVEN** OpenRouter is configured
- **WHEN** operator selects Anthropic or OpenAI profile
- **THEN** runtime uses selected provider after validation
