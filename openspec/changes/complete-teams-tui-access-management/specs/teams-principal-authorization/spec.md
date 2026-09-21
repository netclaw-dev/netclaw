## ADDED Requirements

### Requirement: Principal removal preserves exact scope
The system SHALL remove a global or exact-channel principal only from the selected scope.
The system SHALL preserve other global and exact-channel grants.
The system SHALL describe the resulting effective access before removal of the last applicable restriction.

#### Scenario: A global user is removed
- **WHEN** an operator removes a global user with a remaining channel-specific grant
- **THEN** the system preserves the channel-specific grant

### Requirement: Admission and approval use the same principal result
The system SHALL apply the established principal authorization contract to both Teams ingress and approval callbacks.
The system SHALL retain requester binding, nonce, expiry, replay, and destination checks.

#### Scenario: A group member sends an approval action
- **WHEN** a verified allowed-group member invokes an approval action
- **THEN** the system applies the same principal authorization result as message admission
