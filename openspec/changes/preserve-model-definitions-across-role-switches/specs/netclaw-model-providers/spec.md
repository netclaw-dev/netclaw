## MODIFIED Requirements

### Requirement: Primary and fallback model
The system SHALL support configuring primary, fallback, and compaction roles as references to persistent named model definitions. Changing a role SHALL NOT change the referenced definition. When the primary model is unavailable due to rate limiting, timeout, or error, the system SHALL automatically switch to the fallback model. Fallback activation SHALL be logged for operator visibility.

#### Scenario: Primary model succeeds
- **GIVEN** both primary and fallback roles reference valid definitions
- **WHEN** the primary model responds successfully
- **THEN** the primary model response SHALL be used
- **AND** no fallback activation SHALL occur

#### Scenario: Automatic fallback on primary failure
- **GIVEN** both primary and fallback roles reference valid definitions
- **WHEN** the primary model returns a rate limit, timeout, or error response
- **THEN** the system SHALL retry using the fallback definition
- **AND** a log entry SHALL record the fallback activation with the failure reason

#### Scenario: Role switch preserves model definition
- **GIVEN** a named model definition contains operator capability overrides
- **WHEN** Main or Fallback is assigned to another definition
- **THEN** the previous definition SHALL remain unchanged and available for reassignment
