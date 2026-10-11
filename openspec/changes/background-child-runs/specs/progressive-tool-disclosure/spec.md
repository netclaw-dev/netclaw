## ADDED Requirements

### Requirement: Parent child control remains deferred and absent from child exposure

`check_agent_run` SHALL use Deferred registration and normal audience policy.
Accepted-run context SHALL name it so the parent can call `load_tool` by its exact name.
Start acceptance SHALL NOT automatically expose its schema or change the bounded core set.
Children SHALL NOT discover, load, inherit, or directly dispatch this parent control.
Loading SHALL NOT grant run ownership, requester eligibility, or path access.
This change SHALL NOT expose agent message, child question, steering, or peer tools.

#### Scenario: Parent loads one known control

- **GIVEN** a parent receives accepted-run context with `check_agent_run`
- **WHEN** it loads that exact permitted name
- **THEN** the next parent request can use that control schema
- **AND** unrelated deferred schemas remain absent

#### Scenario: Acceptance does not expand core exposure

- **GIVEN** a parent starts a child
- **WHEN** acceptance commits
- **THEN** the initial core name snapshot remains unchanged
- **AND** the control stays absent until explicit load under normal policy

#### Scenario: Child cannot discover or dispatch parent control

- **GIVEN** a child searches, loads, or directly calls `check_agent_run`
- **WHEN** exposure or dispatch evaluates the child policy
- **THEN** search/load reveals no hidden schema or existence detail
- **AND** direct invocation cannot inspect or cancel any run
