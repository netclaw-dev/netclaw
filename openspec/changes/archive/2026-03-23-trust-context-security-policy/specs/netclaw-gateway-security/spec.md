## MODIFIED Requirements

### Requirement: Default-deny policy

The system SHALL deny interactions unless explicitly allowed by ACL and trust-context policy. Missing or partial security policy SHALL resolve to less capability, not more.

#### Scenario: Unknown sender blocked

- **WHEN** an unknown sender triggers an interaction
- **THEN** the interaction is denied

#### Scenario: Missing trust policy falls back to strict defaults

- **WHEN** a deployment has no explicit trust-context policy for a source or tool
- **THEN** the runtime applies the most restrictive compatible policy
- **AND** capabilities are reduced until the operator configures a broader rule explicitly

### Requirement: Fail-closed startup

The system SHALL fail startup or surface blocking diagnostics when security-critical configuration is invalid or contradictory. Security-critical validation SHALL include posture/exposure mismatches and missing policy required for enabled high-risk capabilities.

#### Scenario: Invalid ACL prevents startup

- **WHEN** ACL schema is invalid
- **THEN** runtime start fails

#### Scenario: Public exposure with unclassified risky capability fails validation

- **WHEN** a public or mixed-trust deployment enables a high-risk tool or MCP server without required trust policy metadata
- **THEN** startup validation fails or produces a blocking doctor result

### Requirement: Shell execution boundaries (SEC-009)

The system SHALL enforce safety boundaries on shell command execution. Shell commands SHALL run under the configured shell mode. `host-allowed` mode SHALL run commands as the process user with no privilege escalation. `sandbox-only` SHALL remain reserved for future isolated execution and SHALL not silently fall back to host execution. A configurable timeout (default 60 seconds) SHALL terminate long-running commands. Output SHALL be truncated at a configurable limit. Interactive commands (those requiring stdin) SHALL be rejected. Working directory SHALL be restricted to configured allowed paths.

#### Scenario: Shell command completes within timeout

- **GIVEN** shell execution timeout is configured to 60 seconds
- **WHEN** a shell command completes in 10 seconds
- **THEN** the output is returned to the session

#### Scenario: Shell command exceeds timeout

- **GIVEN** shell execution timeout is configured to 60 seconds
- **WHEN** a shell command runs for more than 60 seconds
- **THEN** the process is terminated
- **AND** the session receives a timeout error

#### Scenario: Interactive command rejected

- **WHEN** a shell command requires interactive stdin input
- **THEN** execution is rejected before launch
- **AND** the session receives a rejection reason

#### Scenario: Output truncation

- **GIVEN** output truncation limit is configured
- **WHEN** shell command output exceeds the configured limit
- **THEN** output is truncated with an indicator that content was omitted

#### Scenario: Working directory restriction

- **WHEN** a shell command targets a directory outside configured allowed paths
- **THEN** execution is denied with a policy reason code

#### Scenario: Sandbox-only mode without sandbox backend does not widen to host

- **WHEN** shell mode is configured as `sandbox-only` but no sandbox backend exists
- **THEN** shell execution remains unavailable
- **AND** diagnostics explain that host execution was not enabled implicitly

## ADDED Requirements

### Requirement: Verified source and payload taint are evaluated separately
The system SHALL evaluate transport authenticity and payload taint as separate security signals for inbound automation and public-source events.

#### Scenario: Verified public-repo webhook remains tainted
- **WHEN** a signed webhook arrives from a public repository event containing user-controlled text
- **THEN** the runtime records transport authenticity as verified
- **AND** the payload is still treated as public-tainted for trust-context derivation and capability checks

#### Scenario: Private automation event retains narrower taint
- **WHEN** a verified automation event arrives from a private internal source without public user text
- **THEN** the runtime may assign a narrower taint classification than a public event
- **AND** the event still remains bounded by deployment posture and capability policy
