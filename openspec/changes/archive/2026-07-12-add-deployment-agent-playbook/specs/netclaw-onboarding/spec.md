## MODIFIED Requirements

### Requirement: Phase 2 conversational personality bootstrap

The system SHALL trigger conversational identity and mission bootstrap through the initial chat message injected by the init wizard's navigate callback when `LaunchChat()` fires. The message SHALL ask naturally about the operator's communication preferences and working style as well as the deployment mission, successful outcomes, recurring workflows, skill-selection expectations, delegation rules, and known quality failures. It SHALL direct the agent to separate operator/personality context into `SOUL.md` and durable mission/workflow guidance into `AGENTS.md`, propose a concise playbook, obtain operator confirmation, then read and update both files. `TOOLING.md` remains wizard-generated.

#### Scenario: First conversation triggers identity and mission discovery

- **GIVEN** the operator completed the init wizard successfully
- **WHEN** the health check step launches chat via `LaunchChat()`
- **THEN** the agent receives a pre-filled onboarding trigger
- **AND** the trigger asks about both operator context and the deployment's mission, workflows, and failure modes

#### Scenario: Bootstrap writes canonical identity files

- **GIVEN** the onboarding conversation is complete and the operator confirmed the proposed playbook
- **WHEN** the agent persists the results
- **THEN** it reads and updates `SOUL.md` with operator and personality context
- **AND** reads and updates `AGENTS.md` with mission and operating workflow guidance
- **AND** reports that the playbook applies on the next inbound turn

#### Scenario: Wizard preserves an existing mission playbook

- **GIVEN** `AGENTS.md` already contains an operator-authored playbook
- **WHEN** the operator completes init or identity redo
- **THEN** wizard file generation does not overwrite the playbook
- **AND** the conversational trigger instructs the agent to read existing content before proposing changes
