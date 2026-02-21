## ADDED Requirements

### Requirement: Optional smoke test command

The CLI SHALL expose an explicit smoke-test command for live provider checks.

#### Scenario: Run Ollama smoke test

- **WHEN** operator runs `netclaw test smoke --provider ollama`
- **THEN** CLI executes provider connectivity smoke checks
- **AND** outputs a concise pass/fail report
