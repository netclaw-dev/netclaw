## ADDED Requirements

### Requirement: Missing provider configuration does not fail daemon startup

The daemon SHALL start successfully in degraded mode when no valid inference
provider/model configuration is present, serving No-Op chat responses (see
`netclaw-model-providers`). The onboarding wizard's health-check step SHALL
treat this degraded state as a **warn**-level health-check item with
actionable remediation guidance, distinct from a hard startup failure such
as exposure-mode validation rejection.

#### Scenario: Wizard health-check on fresh install with no provider configured

- **GIVEN** the operator runs `netclaw init` and skips provider configuration,
  OR the operator runs the daemon without ever completing onboarding
- **WHEN** the wizard's health-check step starts the daemon
- **THEN** the daemon SHALL come up successfully
- **AND** the wizard SHALL show a **warn**-level health-check item indicating
  no valid provider/model is configured
- **AND** the warn item SHALL include remediation guidance referencing
  `netclaw model` and editing `netclaw.json`
- **AND** the warn item SHALL NOT be presented as a startup failure

#### Scenario: Wizard distinguishes "no provider configured" from exposure-mode failure

- **GIVEN** the daemon starts in degraded mode because no provider is configured
- **WHEN** the wizard reports health-check results
- **THEN** the "no provider configured" item SHALL appear as **warn**
- **AND** the exposure-mode validation item (if present) SHALL be reported
  independently per the existing `Exposure-mode startup validation failure
  shown cleanly` scenario
- **AND** neither item SHALL be collapsed into a generic
  `Daemon did not become ready` message

#### Scenario: Wizard does not silently mask invalid provider configuration as degraded

- **GIVEN** the operator wrote a provider configuration that is malformed
  (schema violation, missing required credential for a declared provider,
  unparseable values)
- **WHEN** the wizard's health-check step starts the daemon
- **THEN** the daemon startup SHALL fail with the existing validation error
- **AND** the wizard SHALL report a **fail**-level item with the validation
  message
- **AND** the wizard SHALL NOT report a warn-level "No-Op active" item in
  place of the validation failure
