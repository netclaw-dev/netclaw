## ADDED Requirements

### Requirement: No-Op chat client fallback when no provider is configured

The system SHALL provide a No-Op `IChatClient` implementation that is selected
by the chat-client provider when configuration validation reports that no
valid provider/model is configured. The No-Op client SHALL allow the daemon
to start successfully in a degraded-but-operational mode rather than failing
host startup. The No-Op client SHALL NOT contact any external service and
SHALL NOT emit tool calls regardless of the tools registered on a request.

#### Scenario: Daemon starts with no provider configured

- **GIVEN** `netclaw.json` contains no inference provider/model configuration
  (fresh install, or provider section absent)
- **WHEN** the daemon starts
- **THEN** host startup SHALL succeed
- **AND** `IChatClientProvider` SHALL resolve to the No-Op client for every
  `ModelRole` (Main, Fallback, Compaction)
- **AND** a single WARN-level log entry SHALL record that the No-Op client
  was selected and reference `netclaw doctor`

#### Scenario: No-Op response contains configuration message and recovery steps

- **GIVEN** the No-Op chat client is active
- **WHEN** any caller invokes the chat client (streaming or non-streaming)
- **THEN** the response content SHALL begin with the exact phrase
  `"No valid model configuration detected."`
- **AND** the response SHALL include the recovery steps `netclaw doctor`,
  `netclaw model`, and editing `netclaw.json`
- **AND** the response SHALL NOT contain any tool calls

#### Scenario: No-Op response references available providers when discoverable

- **GIVEN** the No-Op chat client is active
- **AND** the configuration layer can enumerate known provider profiles
  without performing network I/O
- **WHEN** the No-Op client responds
- **THEN** the response SHALL include a line listing the available provider
  options
- **AND** if no provider profiles can be enumerated, the response SHALL omit
  that line rather than emit a misleading default

#### Scenario: Streaming response delivers the message as a single chunk

- **GIVEN** the No-Op chat client is active
- **WHEN** a caller invokes the streaming chat completion API
- **THEN** the No-Op client SHALL emit the configuration message as a single
  `ChatResponseUpdate` followed by a completion signal
- **AND** the No-Op client SHALL NOT simulate token-by-token streaming

### Requirement: Provider validation distinguishes "no provider configured" from "invalid"

Provider/model configuration validation SHALL produce a tri-state outcome
that the daemon composition root uses to select the chat client:
**valid**, **no provider configured** (non-fatal, selects No-Op), and
**invalid** (fatal, fails startup with the validation error). Malformed
configuration (schema violations, missing required credentials for an
explicitly configured provider, unparseable values) SHALL continue to fail
startup with the existing validation error path and SHALL NOT silently fall
back to the No-Op client.

#### Scenario: Missing provider section selects No-Op

- **GIVEN** the configuration file has no provider/model section
- **WHEN** validation runs
- **THEN** validation SHALL return the "no provider configured" outcome
- **AND** the host SHALL register the No-Op chat client

#### Scenario: Model references unknown provider selects No-Op

- **GIVEN** the configuration file declares one or more providers
- **AND** `Models:Main.Provider` references a provider name that is not in
  the providers dictionary (e.g., typo: `ollama-local1` vs `ollama-local`)
- **WHEN** validation runs
- **THEN** validation SHALL return the "no provider configured" outcome
- **AND** the No-Op banner's "Available providers:" line SHALL list the
  configured provider names so the operator can spot the typo
- **AND** the host SHALL NOT throw an unhandled exception from the
  provider plugin factory

#### Scenario: Malformed provider configuration still fails startup

- **GIVEN** the configuration file declares a provider but omits a required
  credential, contains a schema violation, or contains unparseable values
- **WHEN** validation runs
- **THEN** validation SHALL return the "invalid" outcome
- **AND** the host SHALL fail startup with the existing
  provider-specific validation error
- **AND** the host SHALL NOT fall back to the No-Op client

#### Scenario: Valid configuration uses real provider client

- **GIVEN** the configuration file declares a valid provider and model with
  all required fields present
- **WHEN** validation runs
- **THEN** validation SHALL return the "valid" outcome
- **AND** the host SHALL register the real provider's chat client through
  `NetclawChatClientProvider` (unchanged behavior)

### Requirement: Runtime status reports degraded chat client

The daemon's runtime status wire model (`DaemonRuntimeStatus.Model`) SHALL
include a `Degraded` boolean and a human-readable `DegradedReason` so that
`netclaw status` and any other consumer can render the No-Op state
distinctly. When `Degraded` is true, the overall daemon status SHALL be
reported as `degraded` rather than `healthy`, even if every other
subsystem is fine.

#### Scenario: Status reports degraded chat client and degraded overall

- **GIVEN** the daemon is running with the No-Op chat client active
- **WHEN** a client calls the runtime status endpoint
- **THEN** `Model.Degraded` SHALL be `true`
- **AND** `Model.DegradedReason` SHALL contain the validation reason
  (e.g., the configured-but-unknown provider name)
- **AND** `Overall` SHALL be `degraded`

#### Scenario: `netclaw status` renders degraded model line distinctly

- **GIVEN** the daemon reports `Model.Degraded = true`
- **WHEN** the operator runs `netclaw status`
- **THEN** the model line SHALL clearly indicate the degraded state
  (e.g., `model: (none — No-Op chat client active)`)
- **AND** the status SHALL NOT display the configured-but-broken
  `ModelId`/`Provider` as if they were a live model
- **AND** the output SHALL reference the recovery commands
  (`netclaw doctor`, `netclaw model`)

### Requirement: Chat client provider exposes degraded state for diagnostics

The `IChatClientProvider` contract SHALL expose whether it is operating in
the degraded No-Op mode so that diagnostic surfaces (notably
`netclaw doctor`) can report the state without inspecting concrete types.

#### Scenario: Doctor reports No-Op client active

- **GIVEN** the No-Op chat client is active
- **WHEN** `netclaw doctor` runs the chat-client health check
- **THEN** doctor SHALL report a **warn**-level item indicating that the
  No-Op client is active because no valid provider configuration was
  detected
- **AND** the doctor output SHALL include the recovery commands
  `netclaw model` and editing `netclaw.json`

#### Scenario: Doctor reports real client active

- **GIVEN** a real provider chat client is active
- **WHEN** `netclaw doctor` runs the chat-client health check
- **THEN** doctor SHALL report a **pass**-level item for the chat-client
  check

#### Scenario: Doctor distinguishes degraded from invalid

- **GIVEN** the daemon failed to start due to invalid provider configuration
  (and is therefore not running)
- **WHEN** `netclaw doctor` reports the chat-client check
- **THEN** doctor SHALL surface the validation **fail**-level item from the
  invalid-configuration path
- **AND** the warn-level "No-Op active" item SHALL NOT be reported in that
  case (the daemon never started)

### Requirement: Recovery requires daemon restart

The system SHALL replace the No-Op chat client with the real configured
client only on daemon restart. Hot-swapping the active chat client when
configuration becomes valid mid-process is explicitly out of scope for this
capability.

#### Scenario: Operator fixes configuration and restarts

- **GIVEN** the daemon is running with the No-Op chat client active
- **WHEN** the operator edits `netclaw.json` to add a valid provider/model
- **AND** restarts the daemon
- **THEN** validation SHALL return "valid"
- **AND** the daemon SHALL register the real provider's chat client

#### Scenario: Configuration becomes valid without restart

- **GIVEN** the daemon is running with the No-Op chat client active
- **WHEN** the operator edits `netclaw.json` to add a valid provider/model
- **AND** does NOT restart the daemon
- **THEN** the No-Op chat client SHALL remain active
- **AND** chat turns SHALL continue to return the configuration message
  until restart
