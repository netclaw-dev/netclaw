## ADDED Requirements

### Requirement: Hidden activity observation for adapter heuristics

Input adapters SHALL be allowed to subscribe to session activity outputs such as `ToolCalls` for local progress heuristics without rendering those activity events directly to end users. Adapter-local progress behavior SHALL NOT change the transport-agnostic session actor contract.

#### Scenario: Slack adapter observes tool activity without rendering tool details
- **GIVEN** the Slack adapter subscribes to a session with a filter that includes `ToolCalls`
- **WHEN** the session emits tool-call or tool-result outputs for a turn
- **THEN** the adapter may use those outputs to track whether the turn is actively working
- **AND** it does not post raw tool-call or tool-result details into the Slack thread

#### Scenario: Hidden activity subscription does not affect other subscribers
- **GIVEN** multiple subscribers are attached to the same session
- **WHEN** one adapter widens its filter to observe hidden activity outputs
- **THEN** other subscribers continue receiving only the output categories they requested
- **AND** the session actor does not become transport-specific
