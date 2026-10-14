## ADDED Requirements

### Requirement: Parent guidance supports useful delegation

The embedded identity guidance and the new-install playbook SHALL direct the parent to the logical coordination skill for substantial research, analysis, implementation, and complete artifact tasks. The playbook update SHALL preserve an existing operator file.

The guidance SHALL keep personality in `SOUL.md` and deployment workflow guidance in `AGENTS.md`. Skill guidance SHALL not change runtime authority rules.

#### Scenario: Parent discovers the coordination skill

- **GIVEN** the parent receives a substantial task with useful independent work
- **WHEN** it selects an approach
- **THEN** its identity guidance points to the coordination skill
- **AND** the parent remains responsible for the final result

#### Scenario: Existing operator playbook remains unchanged

- **GIVEN** a deployment has an operator-edited `AGENTS.md`
- **WHEN** an install or upgrade seeds playbook guidance
- **THEN** the existing file remains unchanged

#### Scenario: Guidance does not grant tool authority

- **GIVEN** a child reads the deployment playbook
- **WHEN** it follows delegation guidance
- **THEN** runtime audience and tool policy still apply
- **AND** the guidance does not enable recursive child delegation
