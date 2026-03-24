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

The SecurityPosture step SHALL appear after ChatServices and before ACL in
the wizard flow, so posture defaults are available for channel audience
assignment.

#### Scenario: Step order

- **WHEN** the user completes the ChatServices step
- **THEN** the next step is SecurityPosture
- **AND** after SecurityPosture, the next step is ACL

#### Scenario: Skip when no chat services

- **GIVEN** Slack is disabled in the ChatServices step
- **WHEN** the wizard advances past ChatServices
- **THEN** SecurityPosture is still shown (posture applies to all channels,
  not just Slack)
