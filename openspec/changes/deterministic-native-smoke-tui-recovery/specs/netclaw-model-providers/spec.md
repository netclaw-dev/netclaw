## ADDED Requirements

### Requirement: Required native smoke provider independence

Required native smoke SHALL use an OpenAI-compatible provider configured by the harness.
The required pull-request smoke path SHALL not install an external local-model runtime, start its service, pull a model, or read its smoke model variable.

#### Scenario: Pull-request smoke uses the harness provider

- **GIVEN** a required native smoke job runs without an external local-model runtime
- **WHEN** the harness starts
- **THEN** it configures the OpenAI-compatible provider endpoint and model
- **AND** all broad tapes and scenarios use that provider
