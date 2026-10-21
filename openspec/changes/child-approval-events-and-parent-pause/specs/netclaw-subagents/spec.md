## ADDED Requirements

### Requirement: A child run has its own journal

Each background child run SHALL have its own journal with the persistence ID
`subagent-{escaped session ID}/{run ID}`, where the session ID is URI-escaped. The persistence ID SHALL NOT start with
`session-`, so the child SHALL NOT appear in the session list. The prefix `subagent-{escaped session ID}/` SHALL
match exactly the child runs of that session. The first event of the journal SHALL freeze the exact system prompt and
the resolved model reference. The journal SHALL hold the child conversation: messages, tool calls, and tool results.
The child SHALL write the conversation at its park point and at its end. The journal MAY use snapshots to bound
replay. Each journal type SHALL bind to the project serializer with a manifest and a mapper entry.

#### Scenario: Child journal stays out of the session list

- **GIVEN** a session with a parked child run
- **WHEN** the operator lists sessions
- **THEN** the list does not contain the child journal

#### Scenario: Prefix finds the children of one session

- **GIVEN** sessions `C1/1700` and `C1/1700/x`, each with one child run
- **WHEN** a reader selects persistence IDs with the prefix of session `C1/1700`
- **THEN** it finds only the child of `C1/1700`

### Requirement: A child that needs consent always parks

A background child SHALL park every time a call needs consent. The consent bridge SHALL post the prompt and SHALL
return at once without running the call. When each call of the batch has a result or is parked, the child SHALL record
its conversation and the parked calls, report the park to the parent, and stop after the parent records it. A resumed
child SHALL use the frozen system prompt and model, and the gate SHALL use the current policy.

#### Scenario: Live answer uses the resume path

- **GIVEN** a child parks at a consent request and the daemon does not restart
- **WHEN** the operator approves once
- **THEN** the parent recreates the child from its journal
- **AND** the parked call runs once
