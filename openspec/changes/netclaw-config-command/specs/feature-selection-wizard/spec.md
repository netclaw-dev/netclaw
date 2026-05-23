## REMOVED Requirements

### Requirement: Feature selection wizard step

**Reason**: The init-wizard feature-selection step (issue #1150) had broken
keystroke handling for Team and Public audience toggles. Its responsibility
moves to the new `AudienceProfilesSectionEditor` in `netclaw config`,
which renders per-audience feature toggles with documented arrow-nav and
Space-toggle semantics under a CI-gated smoke tape
(`config-audience.tape`).

**Migration**: Operators previously walked this step at the end of
`netclaw init` for non-Personal postures. After this change, the init
wizard skips the feature-selection step entirely; deployment-wide
defaults are derived from the selected security posture
(per `Requirement: Audience defaults from posture` in the
`channel-audience-tui` capability) and per-audience feature toggles are
edited via `netclaw config → Audience Profiles`. Existing
`netclaw.json` files retain whatever feature-flag values they hold;
the new Audience Profiles editor preserves customizations.

## MODIFIED Requirements

### Requirement: Feature config Enabled flags

The configuration schema SHALL include `Enabled` boolean properties for
Memory, Search, SkillSync, SubAgents, and Webhooks sections, plus a
top-level `Scheduling` section whose only property is `Enabled`. These
flags SHALL be written by either the init wizard's posture-default
cascade or the `AudienceProfilesSectionEditor` in `netclaw config`.
Both writers SHALL emit byte-identical output for equivalent input.

#### Scenario: Disabled memory writes Enabled false

- **GIVEN** the operator disabled memory in the Audience Profiles
  editor (under any audience) and saved
- **WHEN** the editor's merge writer completes
- **THEN** `Memory.Enabled` is `false` in `netclaw.json`

#### Scenario: Disabled search writes Enabled false

- **GIVEN** the operator disabled search in the Audience Profiles
  editor and saved
- **WHEN** the editor's merge writer completes
- **THEN** `Search.Enabled` is `false` in `netclaw.json`

#### Scenario: Disabled scheduling writes top-level Scheduling.Enabled false

- **GIVEN** the operator disabled scheduling in the Audience Profiles
  editor and saved
- **WHEN** the editor's merge writer completes
- **THEN** `Scheduling.Enabled` is `false` in `netclaw.json`
- **AND** `Scheduling` contains no other properties in this change

#### Scenario: Default Personal config has all features enabled

- **GIVEN** the operator selected Personal posture at init
- **WHEN** the init wizard's merge writer completes
- **THEN** all `Enabled` flags default to `true`
