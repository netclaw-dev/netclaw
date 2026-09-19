## ADDED Requirements

### Requirement: Teams executable ingress uses the shared prompt classifier

The Teams adapter SHALL classify every ACL-authorized executable live message
with the shared prompt-injection classifier before it enqueues a session input.
A high-risk classification SHALL not reach the model. A detector failure or an
unavailable detector SHALL fail closed and SHALL not create a model turn. Teams
trust, audience, and provenance decisions SHALL remain separate from prompt
classification.

#### Scenario: High-risk Teams input is blocked before model ingress

- **GIVEN** an ACL-authorized Teams message classified as high risk
- **WHEN** the binding handles the message
- **THEN** it does not enqueue a session input
- **AND** it records a blocked inbound outcome

#### Scenario: Teams detector failure fails closed

- **GIVEN** an ACL-authorized Teams message and an unavailable classifier
- **WHEN** the binding handles the message
- **THEN** it does not enqueue a session input
- **AND** it returns a deterministic unavailable outcome

#### Scenario: Safe Teams input retains its original trust context

- **GIVEN** an ACL-authorized Teams message classified as safe
- **WHEN** the binding enqueues the input
- **THEN** its audience, principal, boundary, provenance, and sender identity remain those resolved by the Teams ACL path
