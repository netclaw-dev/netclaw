## ADDED Requirements

### Requirement: Init wizard approval mode selection

The `netclaw init` wizard SHALL ask about shell approval mode when configuring
each audience profile that has shell access enabled. The wizard SHALL present
three options: Approval (recommended default), Unrestricted (HostAllowed with
no approval), and Off (shell disabled). The selected mode SHALL be written to
the audience profile's `ApprovalPolicy` in `netclaw.json`. For Personal,
selecting Approval SHALL explicitly write
`Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute = "approval"`
rather than relying on runtime audience defaults.

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
warn when the Personal audience enables host shell access without an explicit
`shell_execute` approval gate in `ApprovalPolicy.ToolOverrides`. It SHALL warn
when `tool-approvals.json` contains patterns for audiences or tools that are no
longer configured.

#### Scenario: Doctor warns about Personal host shell without explicit approval gate

- **GIVEN** the Personal audience has host shell access enabled
- **AND** `ApprovalPolicy.ToolOverrides` does not contain `shell_execute`
- **WHEN** `netclaw doctor` runs
- **THEN** it emits a warning that Personal host shell is enabled without an
  explicit `shell_execute` approval gate
- **AND** the warning recommends running `netclaw init` again or setting
  `Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute = "approval"`

#### Scenario: Doctor warns about stale approval patterns

- **GIVEN** `tool-approvals.json` has patterns for `team.shell_execute`
- **AND** the Team audience has shell mode Off
- **WHEN** `netclaw doctor` runs
- **THEN** it emits an info advisory: "Persistent approvals exist for
  team.shell_execute but shell is disabled for Team audience."
