## ADDED Requirements

### Requirement: Sandbox shell diagnostics
The CLI SHALL report whether `sandbox-only` shell execution is actually usable on the current host.

#### Scenario: Doctor reports healthy sandbox backend
- **WHEN** the operator runs `netclaw doctor`
- **AND** shell mode is configured as `sandbox-only`
- **AND** the sandbox backend validates successfully
- **THEN** doctor reports the sandbox shell backend as healthy

#### Scenario: Doctor reports broken sandbox backend
- **WHEN** the operator runs `netclaw doctor`
- **AND** shell mode is configured as `sandbox-only`
- **AND** the sandbox backend is unavailable or misconfigured
- **THEN** doctor reports a failure with remediation guidance
- **AND** the output explains that shell execution will remain unavailable until fixed

#### Scenario: Status shows shell execution mode and backend health
- **WHEN** the operator runs `netclaw status`
- **THEN** the output includes the active shell execution mode
- **AND** when the mode is `sandbox-only`, the output includes sandbox backend health information
