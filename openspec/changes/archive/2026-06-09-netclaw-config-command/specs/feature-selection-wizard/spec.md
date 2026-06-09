## MODIFIED Requirements

### Requirement: Post-install runtime feature editing SHALL move to Enabled Features

Post-install runtime feature editing SHALL move to
`netclaw config -> Security & Access -> Enabled Features`, not to Audience
Profiles.

**Reason**: Runtime feature enablement is deployment-wide and remains a
separate concept from Security Posture and Audience Profiles.

Audience Profiles remains a curated per-audience access editor and SHALL NOT
own per-audience runtime feature toggles.

#### Scenario: Post-install feature editing does not use Audience Profiles

- **GIVEN** the operator wants to change deployment-wide search or memory
  enablement after install
- **WHEN** they use `netclaw config`
- **THEN** the change is made in `Enabled Features`
- **AND** Audience Profiles is not used for that runtime toggle

### Requirement: Feature config Enabled flags

The configuration schema SHALL include deployment-wide `Enabled` flags for
the applicable runtime features. These flags MAY be set during bootstrap
and SHALL be editable post-install through the Enabled Features leaf. The
post-install editor and bootstrap flow SHALL preserve config semantics for
equivalent inputs; byte-identical serialization is not required.

#### Scenario: Enabled Features writes deployment-wide flags

- **GIVEN** the operator disables search in Enabled Features
- **WHEN** the editor saves
- **THEN** `Search.Enabled` is `false` in `netclaw.json`

#### Scenario: Personal posture default keeps all features enabled

- **GIVEN** the operator selected Personal posture during bootstrap
- **WHEN** bootstrap finalizes config
- **THEN** deployment-wide runtime features default to enabled
