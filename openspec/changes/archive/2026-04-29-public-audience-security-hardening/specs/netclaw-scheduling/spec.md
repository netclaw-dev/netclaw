## MODIFIED Requirements

### Requirement: Scheduling runtime config

The system SHALL define a top-level `Scheduling` config section whose only
property in this change is `Enabled`. This section governs reminder/scheduled
execution runtime only and SHALL NOT be interpreted as a background-job shell
execution toggle.

#### Scenario: Scheduling config contains only Enabled

- **WHEN** scheduling config is written to `netclaw.json`
- **THEN** it appears as a top-level `Scheduling` object
- **AND** `Enabled` is the only property introduced by this change

### Requirement: Chat-driven task creation

Scheduling SHALL be controlled by both a deployment-wide runtime switch and
audience/tool allowlists. `Scheduling.Enabled = false` disables reminder
scheduling for all audiences. When runtime-enabled, Public sessions still
require explicit allowlist exposure before they may create, inspect, or mutate
reminders.

#### Scenario: Scheduling runtime-disabled blocks reminder creation

- **GIVEN** `Scheduling.Enabled` is `false` in config
- **WHEN** a Team session attempts to create a reminder or schedule
- **THEN** the scheduling tools are absent or denied
- **AND** no reminder definition is persisted

#### Scenario: Public scheduling remains blocked without explicit allowlist

- **GIVEN** `Scheduling.Enabled` is `true` in config
- **AND** a session has audience `Public`
- **AND** Public does not have the necessary scheduling exposure/grants
- **WHEN** the session attempts to create or inspect a reminder
- **THEN** the scheduling tools are absent or denied

### Requirement: Isolated task execution

Autonomous scheduling/runtime-owned execution SHALL continue using the persisted
originating audience and SHALL NOT widen feature exposure at execution time.

#### Scenario: Scheduled execution does not widen audience after minting

- **GIVEN** a reminder definition was persisted with audience `Public`
- **WHEN** it later executes on schedule
- **THEN** execution uses the stored audience `Public`
- **AND** it does not gain search, memory, skills, subagents, or other
  capabilities that were not exposed to that audience at mint time

#### Scenario: Disabled scheduling runtime prevents execution of persisted reminders

- **GIVEN** reminder definitions already exist on disk
- **AND** `Scheduling.Enabled` is later set to `false`
- **WHEN** the daemon starts
- **THEN** scheduling runtime paths do not execute those reminders until the
  runtime switch is re-enabled

#### Scenario: Background jobs are unaffected by Scheduling.Enabled

- **GIVEN** `Scheduling.Enabled` is `false`
- **WHEN** a Personal shell tool invocation submits a background job
- **THEN** background-job shell infrastructure follows its existing shell/
  background-job policy
- **AND** it is not disabled solely by `Scheduling.Enabled`
