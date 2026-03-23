## MODIFIED Requirements

### Requirement: Shell execution boundaries (SEC-009)

The system SHALL enforce safety boundaries on shell command execution. Shell commands SHALL run under the configured shell mode. `host-allowed` mode SHALL run commands as the process user with no privilege escalation. `sandbox-only` SHALL execute through a configured isolated backend with explicit mount and isolation rules, and it SHALL not silently fall back to host execution. A configurable timeout (default 60 seconds) SHALL terminate long-running commands. Output SHALL be truncated at a configurable limit. Interactive commands (those requiring stdin) SHALL be rejected. Working directory SHALL be restricted to configured allowed paths or mounted sandbox equivalents.

#### Scenario: Shell command completes within timeout
- **GIVEN** shell execution timeout is configured to 60 seconds
- **WHEN** a shell command completes in 10 seconds
- **THEN** the output is returned to the session

#### Scenario: Shell command exceeds timeout
- **GIVEN** shell execution timeout is configured to 60 seconds
- **WHEN** a shell command runs for more than 60 seconds
- **THEN** the process or sandbox invocation is terminated
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

#### Scenario: Sandbox mode enforces isolated backend
- **GIVEN** shell mode is configured as `sandbox-only`
- **WHEN** shell execution is allowed for the request
- **THEN** the command runs only through the configured isolated backend
- **AND** host execution is not used for that invocation

#### Scenario: Sandbox-only mode without backend remains unavailable
- **WHEN** shell mode is configured as `sandbox-only` but no healthy sandbox backend exists
- **THEN** shell execution remains unavailable
- **AND** diagnostics explain that host execution was not enabled implicitly

#### Scenario: Sandbox network is denied by default
- **WHEN** a sandboxed shell command attempts outbound network access under the default policy
- **THEN** the network access is blocked by the sandbox runtime
- **AND** the blocked access is surfaced as command failure details when relevant
