## MODIFIED Requirements

### Requirement: Guided onboarding

`netclaw init` SHALL provide bootstrap-first guided setup. The flow SHALL
collect provider configuration, identity, and security posture. Security
Posture, Enabled Features, and Audience Profiles are distinct concepts.

If the operator selects `Personal`, the bootstrap flow SHALL skip Enabled
Features.

If the operator selects `Team` or `Public`, the bootstrap flow SHALL
automatically continue into Enabled Features before final write.

Audience Profiles editing SHALL NOT be part of init bootstrap; it belongs
to `netclaw config`.

The wizard SHALL continue to write `SOUL.md` and `TOOLING.md`. Identity
remains init-owned in this branch.

#### Scenario: Personal posture skips enabled-features bootstrap step

- **GIVEN** the operator selected `Personal`
- **WHEN** the posture step completes
- **THEN** init does not open an Enabled Features step

#### Scenario: Team posture continues into enabled-features bootstrap step

- **GIVEN** the operator selected `Team`
- **WHEN** the posture step completes
- **THEN** init automatically continues into Enabled Features

#### Scenario: Public posture continues into enabled-features bootstrap step

- **GIVEN** the operator selected `Public`
- **WHEN** the posture step completes
- **THEN** init automatically continues into Enabled Features

### ADDED Requirement: Existing-install init menu

When `netclaw init` runs on an existing install, it SHALL open an action
menu with exactly these options:

- `Redo identity setup`
- `Open configuration editor`
- `Start over from scratch`
- `Cancel`

#### Scenario: Existing install opens action menu

- **GIVEN** `netclaw.json` exists
- **WHEN** the operator runs `netclaw init`
- **THEN** init opens the existing-install menu with the documented four
  options

#### Scenario: Existing install routes to config editor

- **GIVEN** the existing-install menu is open
- **WHEN** the operator chooses `Open configuration editor`
- **THEN** control routes to `netclaw config`

#### Scenario: Existing install routes to init-owned identity flow

- **GIVEN** the existing-install menu is open
- **WHEN** the operator chooses `Redo identity setup`
- **THEN** control routes to the init-owned identity flow

### ADDED Requirement: Start-over flow is double-confirmed

Choosing `Start over from scratch` SHALL open a second dialog with exactly:

- `Reset setup only`
- `Full reset`
- `Cancel`

Either destructive option SHALL require double confirmation before files are
mutated.

#### Scenario: Start-over dialog presents reset choices

- **GIVEN** the existing-install menu is open
- **WHEN** the operator chooses `Start over from scratch`
- **THEN** the second dialog presents `Reset setup only`, `Full reset`, and
  `Cancel`

#### Scenario: Destructive reset requires double confirmation

- **GIVEN** the operator selected either `Reset setup only` or `Full reset`
- **WHEN** the destructive flow proceeds
- **THEN** two distinct confirmations are required before mutation

### ADDED Requirement: No init-force flag in this flow

This bootstrap flow SHALL NOT rely on a `netclaw init --force` mode.
Existing-install reset behavior is owned by the in-TUI existing-install
menu and start-over dialogs.

#### Scenario: Existing-install reset does not require hidden flag

- **GIVEN** an existing install
- **WHEN** the operator wants to restart setup
- **THEN** the path is available from the existing-install init menu
- **AND** it does not depend on `netclaw init --force`
