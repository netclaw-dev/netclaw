## ADDED Requirements

### Requirement: Authorization attempts have one diagnostic correlation identity

For each tool call, the system SHALL create one PII-free authorization-attempt identifier before the first authorization evaluation. The identifier SHALL remain stable through policy evaluation, an agent correction, an interactive approval prompt, the user's decision, a same-call retry, and the terminal authorization or execution outcome. The identifier SHALL be unique to that tool call's authorization lifecycle and SHALL NOT be derived from user content, tool arguments, paths, session identity, or result content.

The authorization-attempt identifier is diagnostic metadata as defined in the [engineering glossary](../../../../../docs/spec/GLOSSARY.md). The system SHALL NOT use it as a grant key, approval response key, policy input, or authority signal. Missing, malformed, or repeated diagnostic metadata SHALL NOT weaken authorization.

#### Scenario: Directly allowed tool call keeps one identifier

- **WHEN** a tool call passes authorization without a correction or prompt
- **THEN** its policy evaluation and terminal outcome contain the same authorization-attempt identifier
- **AND** another tool call receives a different identifier

#### Scenario: Approval retry keeps one identifier

- **GIVEN** a tool call requires interactive approval
- **WHEN** the system emits a prompt, receives an approving decision, and retries that same call
- **THEN** the initial policy result, prompt, decision, retry, and terminal outcome contain the same authorization-attempt identifier
- **AND** the identifier does not satisfy or replace the one-time approval check

#### Scenario: Agent correction closes the original attempt

- **WHEN** a tool call returns a correction such as `UseManagedTemporaryDirectory` or `SetWorkingDirectory`
- **THEN** the policy and correction telemetry contain the original call's authorization-attempt identifier and remediation code
- **AND** a replacement tool call authored by the model starts a new authorization attempt with a different identifier

#### Scenario: Identifier contains no user data

- **GIVEN** a tool call contains a user name, private path, secret-shaped argument, or user-authored text
- **WHEN** the system creates its authorization-attempt identifier
- **THEN** the identifier contains no value copied or encoded from that content

#### Scenario: Diagnostic corruption does not grant authority

- **WHEN** an internal caller supplies a missing, malformed, or duplicate authorization-attempt identifier
- **THEN** ordinary exposure, policy, approval, and grant checks still run
- **AND** the identifier cannot authorize execution

### Requirement: Authorization telemetry reconstructs the approval lifecycle

Structured authorization telemetry SHALL include `AuthorizationAttemptId` at every lifecycle boundary that emits a log: tool start, policy result, correction, approval prompt, approval decision, same-call retry, and terminal result. Each event SHALL also include the provider `CallId`, session identity, and sub-session identity when those values are available at that boundary. Correction events SHALL include the machine-readable remediation code. New correlation telemetry SHALL NOT add raw arguments, command text, result text, paths, requester identity, or other user content.

The parent-session and sub-agent paths SHALL emit the same core field names so an operator can query one authorization-attempt identifier without knowing which actor executed the call.

#### Scenario: Operator follows one prompted attempt

- **GIVEN** a parent-session shell call requires approval
- **WHEN** an operator queries its `AuthorizationAttemptId`
- **THEN** the matching structured events show the tool start, policy result, prompt, decision, retry, and terminal result in lifecycle order
- **AND** those events identify the session and provider call without requiring a search for command text

#### Scenario: Sub-agent uses the same telemetry shape

- **GIVEN** a sub-agent tool call requires parent approval
- **WHEN** the approval is bridged to the parent session
- **THEN** the child start, child policy result, parent-visible prompt, decision, child retry, and child result use the same `AuthorizationAttemptId`
- **AND** child events include the sub-session identity when available

#### Scenario: Concurrent calls remain separable

- **GIVEN** one model response contains multiple tool calls that authorize concurrently
- **WHEN** their telemetry interleaves
- **THEN** each call has a distinct `AuthorizationAttemptId`
- **AND** every event for one call retains that call's identifier

### Requirement: Pending approvals preserve diagnostic correlation across recovery

The system SHALL persist the authorization-attempt identifier with a pending approval. After actor restart or passivation, an approval decision and same-call redrive SHALL reuse the persisted identifier. The identifier SHALL be carried additively so journals written before this change remain readable.

When a legacy pending approval has no valid identifier, recovery SHALL create a fresh diagnostic identifier for the remaining lifecycle. This compatibility repair SHALL NOT modify the pending approval's authority, grant scope, candidate snapshot, or response correlation.

#### Scenario: Cold recovery continues the correlation chain

- **GIVEN** a pending approval was persisted with an authorization-attempt identifier
- **WHEN** the session recovers, receives the decision, and redrives the parked call
- **THEN** the decision, redrive, and terminal result use the persisted identifier

#### Scenario: Legacy pending approval remains usable

- **GIVEN** a journal contains a pending approval written before authorization-attempt identifiers existed
- **WHEN** the session recovers that approval
- **THEN** recovery succeeds and assigns fresh diagnostic correlation for subsequent events
- **AND** the original approval response and authorization rules remain unchanged
