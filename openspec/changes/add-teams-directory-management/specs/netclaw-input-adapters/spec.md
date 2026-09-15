## ADDED Requirements

### Requirement: Teams applies principal authorization before executable ingress

Teams SHALL apply its configured user/group principal authorization after the
existing structural Teams ACL gates and before it creates or routes executable
input. A denied or unavailable membership result SHALL NOT create a session,
binding, model turn, approval authority, or trusted input. The adapter SHALL
retain its existing tenant, team, channel, root, mention, audience, and prompt
classifier behavior.

#### Scenario: Group-denied Teams message cannot reach a model turn

- **GIVEN** a Teams message passes tenant, team, channel, root, mention, and audience checks
- **AND** applicable group authorization denies its sender
- **WHEN** the adapter handles the message
- **THEN** no session or model turn is created
- **AND** the denial uses the stable Teams principal reason

#### Scenario: Existing Teams structural checks still run first

- **GIVEN** a Teams message fails the configured tenant or channel gate
- **WHEN** the adapter handles the message
- **THEN** it is denied by that structural gate
- **AND** no Graph group membership request is made
