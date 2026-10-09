## ADDED Requirements

### Requirement: Short parent delegation route in identity guidance

The platform identity guidance SHALL encourage useful autonomous delegation for substantial code, analysis, research, and complete artifact tasks.
It SHALL route the parent to `agent-coordination` through `skill_load` and bundled resources through `skill_read_resource`.
The new-install playbook scaffold SHALL contain the same short route.
The route SHALL use parent-specific language and SHALL preserve the existing child spawn prohibition and authority rules.
An upgrade SHALL preserve an existing operator playbook.

#### Scenario: Discover coordination without an explicit user instruction
- **GIVEN** the parent receives a substantial task with useful independent analysis and implementation scope
- **WHEN** the parent selects its approach
- **THEN** its identity guidance recommends the logical coordination skill and suitable child work
- **AND** the user need not explicitly request a child

#### Scenario: Preserve an operator playbook
- **GIVEN** the deployment already has an operator-edited `AGENTS.md`
- **WHEN** the init path evaluates the playbook scaffold
- **THEN** the existing file remains unchanged

#### Scenario: Preserve child audience limits
- **GIVEN** a child inherits the deployment playbook
- **WHEN** it reads the parent delegation route
- **THEN** the route does not authorize recursive spawn or parent attachment actions
- **AND** runtime audience and policy gates remain authoritative
