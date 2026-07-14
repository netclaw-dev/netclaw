## ADDED Requirements

### Requirement: Subagent deployment playbook inheritance

Every sub-agent SHALL receive the operating-rules composition for its launch audience: the audience-appropriate embedded operating core followed by the operator-authored deployment `AGENTS.md`. It SHALL NOT inherit `SOUL.md` or `TOOLING.md`. Project-local instructions remain separately scoped to the parent's working directory. Runtime audience, ACL, approval, and tool-policy boundaries SHALL remain unchanged by prompt guidance.

#### Scenario: Personal or Team subagent inherits full core and playbook

- **GIVEN** a Personal or Team parent launches a sub-agent and a deployment playbook exists
- **WHEN** the sub-agent system prompt is assembled
- **THEN** the full embedded operating core appears before the deployment playbook
- **AND** neither `SOUL.md` nor `TOOLING.md` is included

#### Scenario: Public subagent inherits stripped core and playbook

- **GIVEN** a Public parent launches a sub-agent and a deployment playbook exists
- **WHEN** the sub-agent system prompt is assembled
- **THEN** the stripped embedded Public operating core appears before the deployment playbook
- **AND** the same deployment playbook used by other audiences is included

#### Scenario: Subagent prompt layer order remains canonical

- **GIVEN** operating rules, deployment playbook, project instructions, and a sub-agent role prompt are available
- **WHEN** the sub-agent prompt is assembled
- **THEN** their order is embedded core, deployment playbook, project instructions, sub-agent role, then headless execution contract
