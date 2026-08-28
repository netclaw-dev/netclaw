## ADDED Requirements

### Requirement: Required native smoke provider independence

Required native smoke SHALL use an OpenAI-compatible provider configured by the harness.
The required pull-request smoke path SHALL not install Ollama, start Ollama, pull an Ollama model, or read an Ollama smoke model variable.

#### Scenario: Pull-request smoke uses the harness provider

- **GIVEN** a required native smoke job runs without Ollama
- **WHEN** the harness starts
- **THEN** it configures the OpenAI-compatible provider endpoint and model
- **AND** all broad tapes and scenarios use that provider

#### Scenario: Real Ollama coverage remains isolated

- **GIVEN** the independent Ollama contract check runs
- **WHEN** the check selects a real Ollama endpoint
- **THEN** it verifies model discovery and one no-tool completion
- **AND** its failure does not block unrelated pull requests

