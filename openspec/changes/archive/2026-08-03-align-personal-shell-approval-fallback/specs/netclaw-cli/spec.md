## MODIFIED Requirements

### Requirement: Doctor checks for approval configuration

`netclaw doctor` SHALL validate approval configuration consistency. It SHALL
warn when the Personal audience enables host shell access with an exact
`shell_execute = Auto` override. It SHALL NOT warn when the Personal approval
policy is absent. It SHALL NOT warn when the exact shell override is absent.
The runtime resolves both states to `Approval`. The doctor SHALL warn when
`tool-approvals.json` contains patterns for audiences or tools that are no
longer configured.

#### Scenario: Doctor accepts the missing-policy fail-closed fallback

- **GIVEN** the Personal audience has host shell access enabled
- **AND** the Personal profile has no `ApprovalPolicy`
- **WHEN** `netclaw doctor` runs
- **THEN** it emits no warning that shell lacks an approval gate

#### Scenario: Doctor accepts a policy without an exact shell override

- **GIVEN** the Personal audience has host shell access enabled
- **AND** `ApprovalPolicy.ToolOverrides` does not contain `shell_execute`
- **WHEN** `netclaw doctor` runs
- **THEN** it emits no warning that shell lacks an approval gate

#### Scenario: Doctor warns about an explicit Personal shell Auto override

- **GIVEN** the Personal audience has host shell access enabled
- **AND** `ApprovalPolicy.ToolOverrides.shell_execute` is `Auto`
- **WHEN** `netclaw doctor` runs
- **THEN** it emits a warning that Personal host shell explicitly runs without approval
- **AND** the warning recommends changing the exact override to `Approval`

#### Scenario: Doctor warns about stale approval patterns

- **GIVEN** `tool-approvals.json` has patterns for `team.shell_execute`
- **AND** the Team audience has shell mode Off
- **WHEN** `netclaw doctor` runs
- **THEN** it emits an info advisory: "Persistent approvals exist for
  team.shell_execute but shell is disabled for Team audience."
