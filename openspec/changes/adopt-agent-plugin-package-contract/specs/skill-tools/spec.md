## MODIFIED Requirements

### Requirement: Authoritative skill inventory refresh

Every in-process skill inventory refresh SHALL resolve the current enabled native, server-feed, managed-plugin, and external sources.
It SHALL use native greater than server-feed greater than managed-plugin greater than external precedence.
It SHALL update the registry and generated index from the same accepted result.
Concurrent refresh requests SHALL NOT expose a partially rebuilt registry.

#### Scenario: Skill management preserves server-feed inventory

- **GIVEN** a server-feed skill and a native skill are registered
- **WHEN** `skill_manage` successfully mutates the native skill inventory
- **THEN** the refresh retains the server-feed skill
- **AND** the generated index contains both logical skill names

#### Scenario: Newly available feed directory participates in refresh

- **GIVEN** an enabled configured server feed whose managed directory appears after daemon startup
- **WHEN** any inventory refresh occurs
- **THEN** the current feed directory is included in the scan

#### Scenario: Native skill shadows server-feed skill

- **GIVEN** native and server-feed skills have the same logical name
- **WHEN** the inventory is refreshed
- **THEN** the native skill is registered
- **AND** the shadowed server-feed skill is reported through existing scan diagnostics

#### Scenario: Server-feed skill shadows managed plugin skill

- **GIVEN** server-feed and managed plugin skills have the same logical name
- **WHEN** the inventory is refreshed
- **THEN** the server-feed skill is registered
- **AND** the shadowed managed plugin skill is reported through existing scan diagnostics

#### Scenario: Managed plugin skill shadows local external skill

- **GIVEN** managed plugin and local external skills have the same logical name
- **WHEN** the inventory is refreshed
- **THEN** the managed plugin skill is registered
- **AND** the shadowed local external skill is reported through existing scan diagnostics

#### Scenario: Concurrent readers see a complete inventory snapshot

- **GIVEN** sessions can read the skill registry while a background refresh occurs
- **WHEN** the refreshed inventory replaces the previous inventory
- **THEN** each reader observes either the complete previous snapshot or the complete new snapshot
- **AND** no reader observes the registry between clear and repopulation
