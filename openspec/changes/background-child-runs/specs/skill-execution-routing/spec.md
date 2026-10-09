## ADDED Requirements

### Requirement: Routed activations use background acceptance and later result delivery

Every first-party routed activation SHALL use the `background-subagent-runs` start contract.
This includes `skill_load`, slash commands, scheduled slash payloads, and reminder skill activation.
The activation SHALL validate metadata, reload and validate the target, and scan the skill before acceptance.
It SHALL return an accepted run identifier without waiting for final child output.
The later result SHALL preserve skill, profile, original input, and original authority attribution.
The skill body SHALL remain an additive child system overlay on the routed path.
Inline activation and deterministic failure without inline fallback SHALL remain unchanged.

#### Scenario: Tool-driven routed skill returns acceptance

- **GIVEN** an authorized routed skill and a non-empty task
- **WHEN** `skill_load` activates that skill
- **THEN** it returns the accepted run identifier before child completion
- **AND** the later child result arrives through the owner's attributed continuation

#### Scenario: Slash activation frees the parent phase

- **GIVEN** a valid slash skill with `metadata.subagent`
- **WHEN** its child acceptance commits
- **THEN** the parent emits an acceptance reply and completes the slash input
- **AND** a later user input does not wait for that child merely because the skill routed

#### Scenario: Scheduled activation preserves its original correlation

- **GIVEN** an admitted scheduled slash payload
- **WHEN** activation retries after its accepted response disappears
- **THEN** it uses the original admitted occurrence and activation key
- **AND** it creates no second child

#### Scenario: Invalid route cannot become inline work

- **GIVEN** an unknown target, invalid metadata, or rejected skill content
- **WHEN** routed activation runs its pre-admission checks
- **THEN** it returns the existing explicit failure with useful remediation
- **AND** neither a child nor inline execution starts

#### Scenario: A loaded routed skill retains its overlay

- **GIVEN** a scanned routed skill body
- **WHEN** its background child starts
- **THEN** the body remains an additive system overlay
- **AND** acceptance does not inject that body into the parent's inline path
