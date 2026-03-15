## MODIFIED Requirements

### Requirement: Durable memory policy envelope

Every durable anchor, document, record, and edge SHALL carry policy metadata including `audience`, `domain`, `sensitivity`, `recallMode`, `confidence`, `freshness`, and `updateSemantics`. The write path SHALL assign or reject these values before persistence, and the recall path SHALL filter by them before prompt injection.

#### Scenario: Sensitive memory is blocked from auto recall

- **GIVEN** a stored memory item is marked `domain=business`, `sensitivity=secret`, and `recallMode=manual`
- **WHEN** a personal-domain session runs automatic pre-turn recall
- **THEN** the item is excluded from the automatic recall bundle
- **AND** it remains available only to explicit authorized workflows if policy allows

#### Scenario: Audience blocks broader memory from public turn

- **GIVEN** a stored memory item is marked `audience=personal`
- **WHEN** a `public` or `community` turn runs recall
- **THEN** the item is excluded from both automatic recall and inline retrieval unless a higher-trust approval flow authorizes it

### Requirement: Automatic pre-turn recall

The system SHALL execute automatic recall before each user-facing model turn using the latest user message, recent session context, active anchors, and policy scope. Automatic recall SHALL be bounded by a latency budget, SHALL degrade safely when the memory substrate is unavailable, and SHALL respect the active trust context's audience and sensitivity ceiling.

#### Scenario: Recall completes within budget

- **GIVEN** the memory substrate is healthy
- **WHEN** a new turn begins
- **THEN** the session retrieves and injects a bounded recall bundle before the model call
- **AND** the recall operation completes within the configured time budget or degrades safely

#### Scenario: Recall failure degrades without blocking the turn

- **GIVEN** the memory database is temporarily unavailable
- **WHEN** the session starts automatic recall for a turn
- **THEN** the user-facing turn continues without durable recall injection
- **AND** the session records degraded memory status for diagnostics

#### Scenario: Project memory stays scoped within allowed audience

- **GIVEN** two memories share `domain=project:netclaw`
- **AND** one is marked `audience=public` while the other is marked `audience=team`
- **WHEN** a public-facing turn recalls project context
- **THEN** only the `public` memory may be injected
- **AND** shared project scope does not widen visibility beyond the active audience
