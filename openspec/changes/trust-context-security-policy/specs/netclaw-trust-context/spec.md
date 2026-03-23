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
The system SHALL use a shared audience ladder across channels, memories, tools, MCP servers, and outputs. The ordered audiences SHALL be `public`, `team`, and `personal`.

#### Scenario: Public channel cannot access broader audience memory
- **WHEN** a turn originates from a `public` audience source
- **THEN** the runtime excludes memories, tools, and outputs whose minimum audience is broader than `public`

#### Scenario: Team channel can access team-scoped resources only
- **WHEN** a turn originates from a `team` audience source
- **THEN** the runtime may expose `public` and `team` resources that also satisfy other policy checks
- **AND** the runtime excludes `personal` resources unless an explicit approval flow widens them

### Requirement: Audience selects a resolved policy profile
The system SHALL map each effective audience to a resolved policy profile that defines the maximum resource scope available to the turn. Trust downgrades SHALL switch evaluation to the narrower audience profile rather than partially widening a broader profile.

#### Scenario: Public downgrade switches to public profile
- **GIVEN** a session started from a `personal` audience with a broader personal profile
- **WHEN** the working context narrows to `public`
- **THEN** tool and resource authorization are evaluated against the resolved `public` profile
- **AND** personal-only filesystem roots, publish destinations, and tools are no longer available during the downgraded context

#### Scenario: Missing audience profile falls back to stricter resolved policy
- **WHEN** a configured audience profile is missing or incomplete
- **THEN** the runtime resolves the effective profile to the strictest compatible defaults for that audience
- **AND** the missing fields do not inherit hidden permissive access from broader audiences

### Requirement: Security boundary is a runtime-owned partition distinct from audience
The system SHALL derive and carry a runtime-owned security boundary for each turn and durable memory item. The security boundary SHALL determine which memories may be reused across channels or sessions, while `domain` continues to describe what the memory is about.

#### Scenario: Personal boundary allows cross-channel project recall
- **GIVEN** durable Netclaw repository memories were formed in an owner DM and marked with the same `personal` security boundary as a later private Slack channel session
- **WHEN** the owner asks about the Netclaw repository in that private channel
- **THEN** automatic recall may reuse the DM-formed project memories inside the shared boundary
- **AND** channel/session identity alone does not hide the reusable knowledge

#### Scenario: Team boundary does not expose personal-boundary memories
- **GIVEN** a memory is stored under a `personal` security boundary
- **WHEN** a `team` turn searches for the same subject domain
- **THEN** the personal-boundary memory is excluded even if the subject/domain matches

### Requirement: Trust context can auto-downgrade but not auto-upgrade
The system SHALL allow trust context to narrow automatically as the bot enters risky working contexts, but SHALL NOT widen authority automatically after a downgrade.

#### Scenario: Sensitive-read subtask narrows authority
- **WHEN** a trusted operator turn enters a sensitive-read subtask such as inspecting email content
- **THEN** the runtime narrows the active trust context for the duration of that subtask
- **AND** dangerous capabilities such as shell execution, sensitive recall, and publish-external actions are re-evaluated against the narrower context

#### Scenario: Return from downgraded context requires approval or fresh trusted turn
- **WHEN** a downgraded working context requests a broader capability than it currently allows
- **THEN** the runtime requires explicit operator approval or a fresh trusted operator turn before widening authority
