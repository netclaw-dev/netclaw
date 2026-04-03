## ADDED Requirements

### Requirement: Init wizard approval mode selection

The `netclaw init` wizard SHALL ask about shell approval mode when configuring
each audience profile that has shell access enabled. The wizard SHALL present
three options: Approval (recommended default), Unrestricted (HostAllowed with
no approval), and Off (shell disabled). The selected mode SHALL be written to
the audience profile's `ApprovalPolicy` in `netclaw.json`.

#### Scenario: Init wizard prompts for Personal shell mode

- **GIVEN** the user is running `netclaw init`
- **WHEN** the wizard configures the Personal audience profile
- **AND** shell mode is not Off
- **THEN** the wizard asks: "Shell approval mode for Personal?"
- **AND** offers Approval (default), Unrestricted, and Off

#### Scenario: Init wizard skips approval for audiences with shell off

- **GIVEN** the user is running `netclaw init`
- **WHEN** the wizard configures an audience with shell mode Off
- **THEN** the wizard does NOT ask about approval mode for that audience

#### Scenario: Selection written to config

- **GIVEN** the user selects "Approval" for Personal audience
- **WHEN** the wizard writes the config
- **THEN** `netclaw.json` includes
  `Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute = "approval"`

### Requirement: Doctor checks for approval configuration

`netclaw doctor` SHALL validate approval configuration consistency. It SHALL
warn when approval mode is enabled for a tool on an audience but the primary
channel does not support interactive approval. It SHALL warn when
`tool-approvals.json` contains patterns for audiences or tools that are no
longer configured.

#### Scenario: Doctor warns about approval on unsupported channel

- **GIVEN** `shell_execute` is in Approval mode for Personal
- **AND** the only active channel is headless
- **WHEN** `netclaw doctor` runs
- **THEN** it emits a warning: "Approval mode enabled for shell_execute but
  active channel does not support interactive approval. Tool calls will be
  auto-denied."

#### Scenario: Doctor warns about stale approval patterns

- **GIVEN** `tool-approvals.json` has patterns for `team.shell_execute`
- **AND** the Team audience has shell mode Off
- **WHEN** `netclaw doctor` runs
- **THEN** it emits an info advisory: "Persistent approvals exist for
  team.shell_execute but shell is disabled for Team audience."
