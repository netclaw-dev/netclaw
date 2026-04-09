# netclaw-config-hot-reload Delta Spec

## ADDED Requirements

### Requirement: Seam invariant agreement across reload validation
Hot reload validation SHALL enforce the same provider, channel, auth, and notification seam invariants as schema validation, doctor, and startup validation.

#### Scenario: Reload rejects provider seam invariant violation
- **WHEN** a hot reload update introduces an unknown provider kind or invalid provider-auth configuration
- **THEN** the update is rejected with explicit remediation
- **AND** the last valid runtime configuration remains in effect

#### Scenario: Reload rejects channel or notification seam invariant violation
- **WHEN** a hot reload update introduces an unknown channel kind or invalid notification target kind
- **THEN** the update is rejected with explicit remediation
- **AND** no partial runtime activation occurs for the invalid seam state

### Requirement: Hot reload does not silently fallback across seam kinds
When seam-related hot reload validation fails, the system SHALL NOT silently substitute another provider module, another channel module, or another auth mode.

#### Scenario: Invalid seam reload does not switch modules
- **WHEN** a hot reload update invalidates the currently requested provider, channel, or auth mode
- **THEN** the runtime retains the last valid configuration
- **AND** the system does not silently switch to a fallback provider, fallback channel, or fallback auth path
