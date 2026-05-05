## MODIFIED Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`. On completion, the
wizard SHALL run a health check to verify the baseline configuration is functional.
If daemon startup fails because configuration validation rejects the selected
exposure mode or remote-auth topology, the wizard SHALL surface that failure as a
structured setup error with remediation guidance.

#### Scenario: Exposure-mode startup validation failure shown cleanly

- **GIVEN** the operator completes `netclaw init`
- **AND** the written configuration causes `ExposureModeValidationService` to reject
  daemon startup
- **WHEN** the health-check step starts the daemon
- **THEN** the wizard shows a failed health-check item containing the validation
  message
- **AND** the wizard includes remediation guidance for fixing the exposure/auth
  configuration
- **AND** the operator is not shown a raw stack trace

#### Scenario: Startup validation failure does not degrade to generic readiness timeout

- **GIVEN** daemon startup fails immediately because exposure validation rejects the
  configuration
- **WHEN** the health-check step polls daemon readiness
- **THEN** the wizard reports the actual startup validation failure
- **AND** it does NOT report only "Daemon did not become ready" unless the failure
  reason is genuinely unavailable
