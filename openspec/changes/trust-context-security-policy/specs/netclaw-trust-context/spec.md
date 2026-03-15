## ADDED Requirements

### Requirement: Effective trust context derivation
The system SHALL derive an effective trust context for every inbound turn from runtime-owned inputs including deployment posture, source audience, principal classification, source provenance, and active working-context downgrades.

#### Scenario: Owner turn in personal deployment derives personal context
- **WHEN** the owner sends a direct message to a personal deployment through a trusted private channel
- **THEN** the runtime derives an effective trust context no broader than `personal`
- **AND** the turn is eligible to use capabilities allowed by the deployment ceiling and current policy

#### Scenario: Teammate DM to personal bot narrows context
- **WHEN** a non-owner teammate sends a direct message to an owner's personal bot
- **THEN** the runtime derives an effective trust context no broader than `team`
- **AND** the turn does not inherit the owner's personal-only capabilities or memory audiences

### Requirement: Audience is a cross-cutting visibility boundary
The system SHALL use a shared audience ladder across channels, memories, tools, MCP servers, and outputs. The ordered audiences SHALL be `public`, `community`, `team`, `personal`, and `operator`.

#### Scenario: Public channel cannot access broader audience memory
- **WHEN** a turn originates from a `public` audience source
- **THEN** the runtime excludes memories, tools, and outputs whose minimum audience is broader than `public`

#### Scenario: Team channel can access team-scoped resources only
- **WHEN** a turn originates from a `team` audience source
- **THEN** the runtime may expose `public`, `community`, and `team` resources that also satisfy other policy checks
- **AND** the runtime excludes `personal` and `operator` resources unless an explicit approval flow widens them

### Requirement: Trust context can auto-downgrade but not auto-upgrade
The system SHALL allow trust context to narrow automatically as the bot enters risky working contexts, but SHALL NOT widen authority automatically after a downgrade.

#### Scenario: Sensitive-read subtask narrows authority
- **WHEN** a trusted operator turn enters a sensitive-read subtask such as inspecting email content
- **THEN** the runtime narrows the active trust context for the duration of that subtask
- **AND** dangerous capabilities such as shell execution, sensitive recall, and publish-external actions are re-evaluated against the narrower context

#### Scenario: Return from downgraded context requires approval or fresh trusted turn
- **WHEN** a downgraded working context requests a broader capability than it currently allows
- **THEN** the runtime requires explicit operator approval or a fresh trusted operator turn before widening authority
