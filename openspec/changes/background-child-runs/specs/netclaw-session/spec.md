## ADDED Requirements

### Requirement: Parent context carries recorded child run status

The owning session SHALL include a bounded status summary for its accepted and undelivered child runs in parent context.
The summary SHALL provide machine-actionable run IDs and the exact deferred control name.
It SHALL identify recorded state and distinguish cancellation admission from local dispatch closure.
It SHALL NOT expose hidden child task text, unsafe paths, or another session's run details.
It SHALL NOT claim live provider state that the owner did not observe.
Terminal child admission SHALL preserve the original task's recurrence state and current parent directory/project/branch facts.
Confirmed child file activity SHALL merge through the existing working-context rules.

#### Scenario: A later turn can inspect its earlier child

- **GIVEN** the parent accepted a child in an earlier turn
- **WHEN** a later eligible parent turn assembles context
- **THEN** it receives that run's recorded status and control name
- **AND** it can request cancellation through ordinary deferred-tool use

#### Scenario: Child activity does not overwrite current parent scope

- **GIVEN** the parent changed directory after the child start
- **WHEN** the owner admits a child result with confirmed file activity
- **THEN** the parent's current directory, project, and branch remain unchanged
- **AND** admitted child activity retains its own run attribution

#### Scenario: Result admission does not reset recurrence evidence

- **GIVEN** the original parent task retains unresolved recurrence evidence
- **WHEN** a child completion admits an internal continuation
- **THEN** it preserves that task identity and evidence under the first PR's contract
- **AND** it does not create a fresh authorized task solely from child text
