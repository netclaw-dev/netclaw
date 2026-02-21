# netclaw-operator-ui Specification

## Purpose

Define operator UI behavior for the ops console and security visibility.

## Requirements

### Requirement: Operations-first overview

The UI SHALL provide a dense overview of runtime and security state.

#### Scenario: Overview dashboard load

- **WHEN** an operator opens the overview
- **THEN** gateway health, Slack status, persistence status, and policy deny
  counters are visible

### Requirement: Session inspection

The UI SHALL allow searching and inspecting sessions by thread key.

#### Scenario: Inspect a thread session

- **WHEN** operator selects a session
- **THEN** turn timeline, compaction status, and recovery metadata are shown

### Requirement: Policy editing with validation

The UI SHALL validate ACL changes before apply.

#### Scenario: Invalid ACL edit

- **WHEN** operator enters invalid ACL JSON
- **THEN** apply is blocked
- **AND** validation errors are shown with field-level guidance

### Requirement: Security posture visibility

The UI SHALL surface exposure mode and approval state prominently.

#### Scenario: Public exposure warning

- **WHEN** system runs in public exposure mode
- **THEN** security page shows elevated warning status
