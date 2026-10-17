# agent-coordination Specification

## Purpose

This capability gives the parent reusable guidance for scoped delegation,
artifact exchange, independent review, and evidence-based completion.

Source PRDs: [PRD-001 FR-013](../../../docs/prd/PRD-001-netclaw-mvp.md#fr-013-capability-self-discovery),
[PRD-004 CLI-001A](../../../docs/prd/PRD-004-cli-onboarding-and-config.md#cli-001a-guided-setup-wizard), and
[PRD-007](../../../docs/prd/PRD-007-agent-personality-and-local-memory.md).

Use these [engineering glossary](../../../docs/spec/GLOSSARY.md) terms:
[subagent](../../../docs/spec/GLOSSARY.md#subagent),
[authority](../../../docs/spec/GLOSSARY.md#authority), and
[artifact directory](../../../docs/spec/GLOSSARY.md#artifact-directory).

## Requirements

### Requirement: Coordination guidance composes existing tools

The coordination skill SHALL guide the parent to divide substantial work into independent outcomes, give each child a bounded task and authorized context, and use existing tools to exchange artifacts. The guidance SHALL NOT create authority, require fixed artifact names, or require a fixed child count or order.

The parent SHALL own the final result. It SHALL review child evidence and state unresolved failures before it reports completion.

#### Scenario: Parent assigns independent tasks

- **GIVEN** a substantial task has independent research or implementation outcomes
- **WHEN** the parent loads the coordination skill
- **THEN** the skill guides it to assign bounded tasks with clear return expectations
- **AND** it leaves shared-write ownership explicit

#### Scenario: Parent reviews child evidence

- **GIVEN** a child returns an artifact and evidence
- **WHEN** the parent prepares its final result
- **THEN** it checks the evidence and the requested outcome
- **AND** it does not claim completion for unresolved work

#### Scenario: Child instructions do not widen authority

- **GIVEN** the skill suggests a child task or artifact path
- **WHEN** the child acts on that suggestion
- **THEN** runtime policy still controls file, shell, and approval authority
- **AND** the suggestion grants no new access

### Requirement: Retained output hint supports focused child inspection

When a tool output is retained and the child can read its reference under current policy, the parent guidance SHALL suggest focused child inspection for a question. The hint SHALL identify the usable reference. It SHALL NOT start a child automatically.

The guidance SHALL omit the inspection suggestion when the output was not retained or when the child cannot read the reference. It SHALL NOT add another size threshold.

#### Scenario: Retained output supports a focused question

- **GIVEN** a tool output is retained and the child can access its reference
- **WHEN** the parent receives the spill response
- **THEN** the response suggests a focused inspection task with that reference
- **AND** the parent decides whether to delegate

#### Scenario: Missing retention does not suggest inspection

- **GIVEN** the tool output was not retained or the child lacks access
- **WHEN** the parent receives the tool response
- **THEN** the response does not suggest child inspection of that output
- **AND** it does not imply that the child can access it

### Requirement: Coordination guidance uses logical skill access

The runtime SHALL expose coordination guidance through its logical skill name and safe relative resource paths. Guidance SHALL NOT teach the model to derive or use a physical skill root.

#### Scenario: Parent loads the coordination skill

- **GIVEN** the coordination skill is installed and available to the parent
- **WHEN** the parent requests coordination guidance
- **THEN** it can load the skill by its logical name
- **AND** it can read an optional resource by its safe relative path
