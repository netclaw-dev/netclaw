## MODIFIED Requirements

### Requirement: Durable memory policy envelope

Every durable anchor, document, record, and edge SHALL carry policy metadata including `audience`, `boundary`, `domain`, `sensitivity`, `recallMode`, `confidence`, `freshness`, and `updateSemantics`. The write path SHALL assign or reject these values before persistence, and the recall path SHALL filter by them before prompt injection.

#### Scenario: Sensitive memory is blocked from auto recall

- **GIVEN** a stored memory item is marked `domain=business`, `sensitivity=secret`, and `recallMode=manual`
- **WHEN** a personal-domain session runs automatic pre-turn recall
- **THEN** the item is excluded from the automatic recall bundle
- **AND** it remains available only to explicit authorized workflows if policy allows

#### Scenario: Audience blocks broader memory from public turn

- **GIVEN** a stored memory item is marked `audience=personal`
- **AND** the item has not already been surfaced into the current session history
- **WHEN** a `public` turn runs recall
- **THEN** the item is excluded from future automatic recall and inline retrieval unless a higher-trust approval flow authorizes it

#### Scenario: Mid-session downgrade limits future recall but does not unsurface prior memory

- **GIVEN** a higher-trust turn has already surfaced a `personal` durable fact into the current session history
- **WHEN** the active trust context later downgrades to `public`
- **THEN** future automatic recall and inline retrieval MUST exclude additional `personal` memories unless a higher-trust approval flow authorizes them
- **AND** the runtime does not rely on per-turn recall filtering to retroactively remove the already surfaced fact from session history

#### Scenario: Raw secret material is rejected during memory formation

- **GIVEN** memory formation receives content containing a raw credential, private key, bearer token, API key, or similarly sensitive secret value
- **WHEN** the write path evaluates the candidate
- **THEN** the candidate is rejected or sanitized before persistence
- **AND** the raw secret value is never stored as durable memory regardless of audience, boundary, or explicit request

#### Scenario: Sanitized summary may survive secret rejection

- **GIVEN** a turn includes sensitive material plus a useful non-secret operational fact
- **WHEN** the memory formation pipeline can safely separate the fact from the secret value
- **THEN** the system may persist a sanitized summary of the fact
- **AND** the secret value itself is omitted or redacted

#### Scenario: Boundary allows project recall across channels without widening exposure

- **GIVEN** two durable memories share `domain=project:netclaw`
- **AND** both are stored inside the same `personal` security boundary even though they were formed in different Slack channels or local sessions
- **WHEN** a later `personal` turn recalls project context
- **THEN** the runtime may retrieve both memories through the shared boundary
- **AND** it does not require channel/session identity to match the formation source

### Requirement: Automatic pre-turn recall is admission control, not retroactive redaction

The system SHALL execute automatic recall before each user-facing model turn using the latest user message, recent session context, active anchors, and policy scope. Automatic recall SHALL be bounded by a latency budget, SHALL degrade safely when the memory substrate is unavailable, and SHALL respect the active trust context's audience and sensitivity ceiling for memories that have not yet been surfaced into the active session. The runtime SHALL treat recall filtering as admission control for new memory disclosure, not as a retroactive redaction mechanism for content already persisted in the session history.

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
- **AND** shared project scope does not widen visibility beyond the active audience or boundary
