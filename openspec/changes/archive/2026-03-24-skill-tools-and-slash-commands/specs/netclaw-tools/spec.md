# netclaw-tools Delta Spec

## ADDED Requirements

### Requirement: Skill tool registration

The system SHALL register `skill_load`, `skill_read_resource`, and
`skill_manage` as first-party tools at startup with `Grant = "builtin"`.
All three tools SHALL be available to all trust audiences.

#### Scenario: Skill tools registered at startup

- **WHEN** the Netclaw process starts
- **THEN** `skill_load`, `skill_read_resource`, and `skill_manage` are
  registered as MEAI tool definitions
- **AND** each has `Grant = "builtin"`
- **AND** each is available in Public, Team, and Personal audiences
