## ADDED Requirements

### Requirement: Configurable sandbox shell backend
The system SHALL provide a dedicated sandbox shell backend configuration for `sandbox-only` execution. The configuration SHALL identify the backend type, launch prerequisites, runtime image or equivalent sandbox payload, scratch workspace root, and default isolation settings needed to validate and launch the backend safely.

#### Scenario: Sandbox backend configured successfully
- **WHEN** the operator configures a supported sandbox backend with all required fields
- **THEN** Netclaw accepts the configuration
- **AND** the backend is available for `sandbox-only` shell execution

#### Scenario: Missing backend prerequisites rejected
- **WHEN** `sandbox-only` execution is configured without required backend settings
- **THEN** validation fails
- **AND** diagnostics identify the missing sandbox prerequisite fields

### Requirement: Ephemeral isolated workspace per invocation
The system SHALL execute each sandboxed shell invocation inside an isolated per-invocation workspace. The workspace SHALL expose only explicitly mounted paths derived from the resolved project/session context and sandbox policy, plus a dedicated scratch area for writable outputs.

#### Scenario: Project command runs in mounted workspace
- **GIVEN** a sandboxed shell request targets a registered project
- **WHEN** the invocation starts
- **THEN** the sandbox sees the configured project workspace mount
- **AND** the command runs with its working directory set to the mounted project path inside the sandbox

#### Scenario: Host path outside allowed mounts is unavailable
- **WHEN** a sandboxed shell command attempts to access a host path that was not mounted into the sandbox
- **THEN** the access fails inside the sandbox
- **AND** Netclaw does not widen the mount set for that invocation automatically

### Requirement: Network-denied sandbox default
Sandbox shell execution SHALL deny outbound network access by default.

#### Scenario: Network access blocked by default
- **WHEN** a sandboxed shell command attempts outbound network access under the default sandbox policy
- **THEN** the network operation fails inside the sandbox
- **AND** the failure does not cause the command to be retried on the host

### Requirement: Deterministic cleanup of sandbox artifacts
The system SHALL remove per-invocation sandbox artifacts after completion or failure, and it SHALL attempt cleanup of orphaned sandbox artifacts left by prior crashes when the daemon starts.

#### Scenario: Successful invocation cleans up artifacts
- **WHEN** a sandboxed shell invocation completes successfully
- **THEN** the temporary sandbox runtime artifacts are removed
- **AND** only explicit output returned through allowed mounts remains available to the session

#### Scenario: Startup removes stale artifacts
- **WHEN** the daemon starts after a previous crash left stale sandbox artifacts
- **THEN** Netclaw attempts cleanup of the orphaned sandbox artifacts before accepting new sandbox executions
