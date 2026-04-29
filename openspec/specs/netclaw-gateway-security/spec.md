## ADDED Requirements

### Requirement: Inbound webhook ingress safeguards

The system SHALL enforce webhook ingress safeguards before dispatching any agent
work. For configured webhook routes, request verification, request-size limits,
delivery deduplication, and rate limiting SHALL all happen before a session is
created.

#### Scenario: Invalid verifier input rejected before session launch

- **GIVEN** a configured webhook route requires request verification
- **WHEN** an inbound request arrives with a missing or invalid signature/secret
- **THEN** the daemon rejects the request
- **AND** no webhook session is created

#### Scenario: Duplicate delivery suppressed before dispatch

- **GIVEN** a webhook route extracts a delivery identifier from the inbound
  request
- **AND** the same delivery identifier has already been accepted recently
- **WHEN** the duplicate request arrives again
- **THEN** the daemon suppresses the duplicate delivery
- **AND** no second webhook session is created

#### Scenario: Oversized webhook request rejected

- **GIVEN** a configured webhook route has a maximum request size
- **WHEN** an inbound request exceeds that size limit
- **THEN** the daemon rejects the request before payload dispatch

#### Scenario: Route-level rate limit exceeded

- **GIVEN** a configured webhook route has reached its allowed delivery rate
- **WHEN** another request arrives for that route
- **THEN** the daemon rejects the request with a rate-limit response
- **AND** no webhook session is created

#### Scenario: Invalid route file fails closed before dispatch

- **GIVEN** a route file exists for a webhook route but is malformed or invalid
- **WHEN** a request arrives for that route
- **THEN** the daemon does not use any stale cached route definition
- **AND** the request is rejected before a webhook session is created
