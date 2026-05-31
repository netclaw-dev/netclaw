# security-posture-tui Specification

## Purpose

Define the interactive TUI step for deployment posture selection during
`netclaw init`.

## Requirements

### Requirement: Security posture selection step

The wizard SHALL present an interactive step where the user selects a
deployment posture (Personal, Team, or Public) with explanatory text for
each option.

#### Scenario: User selects Personal posture

- **GIVEN** the wizard is at the SecurityPosture step
- **WHEN** the user selects "Personal"
- **THEN** deployment posture is set to Personal
- **AND** shell execution mode defaults to HostAllowed
- **AND** DM audience defaults to Personal
- **AND** channel audience defaults to Team

#### Scenario: User selects Team posture

- **GIVEN** the wizard is at the SecurityPosture step
- **WHEN** the user selects "Team"
- **THEN** deployment posture is set to Team
- **AND** shell execution mode defaults to Off
- **AND** DM audience defaults to Team
- **AND** channel audience defaults to Team

#### Scenario: User selects Public posture

- **GIVEN** the wizard is at the SecurityPosture step
- **WHEN** the user selects "Public"
- **THEN** deployment posture is set to Public
- **AND** shell execution mode defaults to Off
- **AND** DM audience defaults to Public
- **AND** channel audience defaults to Public

### Requirement: Posture step position in wizard flow

The SecurityPosture step SHALL appear after ChatServices and before the
Feature Selection step in the wizard flow. For non-Personal postures, the
Feature Selection step SHALL appear immediately after SecurityPosture so
that feature availability is configured before channel audience assignment.

#### Scenario: Step order with Feature Selection

- **WHEN** the user completes the SecurityPosture step
- **AND** the selected posture is Team or Public
- **THEN** the next step is Feature Selection
- **AND** after Feature Selection, the next applicable step follows

#### Scenario: Step order without Feature Selection

- **WHEN** the user completes the SecurityPosture step
- **AND** the selected posture is Personal
- **THEN** the Feature Selection step is skipped
- **AND** the next applicable step follows directly
