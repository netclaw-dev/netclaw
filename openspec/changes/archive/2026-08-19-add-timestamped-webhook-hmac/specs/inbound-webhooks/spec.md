## MODIFIED Requirements

### Requirement: Verification kinds are generic and minimal

Route verification SHALL be modeled as generic verification kinds rather than
one first-class verifier type per provider. The system SHALL support generic
body-only HMAC verification, timestamped HMAC verification, and shared-header
secret verification. Existing body-only HMAC and shared-header secret routes
SHALL retain their current behavior and defaults when timestamped-HMAC support
is introduced.

#### Scenario: Generic HMAC verification is configured

- **GIVEN** a route file configures HMAC verification with header metadata and a
  shared secret
- **WHEN** a request arrives with a valid matching signature
- **THEN** the route verification succeeds without requiring a provider-specific
  verifier type

#### Scenario: Timestamped HMAC verification is configured

- **GIVEN** a route explicitly configures timestamped HMAC verification
- **WHEN** a request arrives with a valid structured signature over the timestamp
  and raw request body
- **THEN** the route verification succeeds without requiring a provider-specific
  verifier type

#### Scenario: Shared-header secret verification is configured

- **GIVEN** a route file configures shared-header secret verification
- **WHEN** a request arrives with the expected secret header value
- **THEN** the route verification succeeds without requiring a provider-specific
  verifier type

#### Scenario: Existing route omits timestamped settings

- **GIVEN** a route file created before timestamped-HMAC support selects `Hmac`
  or `HeaderSecret`
- **WHEN** the upgraded daemon loads and verifies that route
- **THEN** the route uses the same verifier, defaults, and signed bytes as before
- **AND** no migration or automatic verifier selection occurs

## ADDED Requirements

### Requirement: Timestamped HMAC verification is replay bounded

Timestamped HMAC verification SHALL parse one timestamp and one or more
signatures from the configured structured header, compute HMAC-SHA256 over the
exact received timestamp text, configured separator, and raw request body, and
accept the request only when at least one signature matches using constant-time
comparison. The timestamp SHALL be within the configured tolerance of the
daemon's current time; the default tolerance SHALL be 300 seconds.

#### Scenario: Valid timestamped signature is accepted

- **GIVEN** a timestamped-HMAC route using the default `t`, `v1`, `.`, and
  300-second settings
- **WHEN** a request contains a matching signature and a timestamp within the
  tolerance window
- **THEN** verification succeeds

#### Scenario: Any rotation signature may match

- **GIVEN** a structured signature header contains multiple `v1` signatures
- **WHEN** any one signature matches the configured secret and signed payload
- **THEN** verification succeeds

#### Scenario: Stale or future timestamp is rejected

- **GIVEN** a request has a cryptographically valid timestamped signature
- **WHEN** its timestamp is more than the configured tolerance before or after
  the daemon's current time
- **THEN** verification fails with a timestamp-out-of-tolerance reason
- **AND** no webhook session is dispatched

#### Scenario: Malformed structured signature is rejected

- **WHEN** the configured signature header is missing, malformed, contains an
  ambiguous timestamp, an invalid Unix timestamp, or no valid signature values
- **THEN** verification fails cleanly
- **AND** the endpoint returns its normal unauthorized response without crashing

### Requirement: Timestamped verification configuration is additive

The route schema, CLI, and `set_webhook` tool SHALL expose timestamp field,
signature field, signed-payload separator, and tolerance settings as optional
configuration for timestamped HMAC routes. Body-only HMAC SHALL remain the
default verification kind, and the system SHALL NOT infer or fall back between
verification kinds. Effective timestamp and signature field names SHALL be
distinct HTTP tokens. Undefined numeric verifier-kind or HMAC-algorithm values
SHALL be rejected during route validation before request handling.

#### Scenario: Existing route is updated without timestamp options

- **GIVEN** an existing body-only HMAC, timestamped-HMAC, or shared-header secret
  route
- **WHEN** an operator updates an unrelated route property using the CLI or
  `set_webhook` without supplying optional verification settings
- **THEN** the stored verifier kind and settings remain unchanged
- **AND** timestamped-HMAC properties are not introduced into the route file

#### Scenario: Concurrent tool updates are serialized

- **GIVEN** two authorized `set_webhook` invocations concurrently update the
  same existing route
- **WHEN** each invocation reads, patches, validates, and saves the definition
- **THEN** those operations execute atomically under the route store lock
- **AND** neither invocation overwrites fields retained from the other's update

#### Scenario: Unrepresentable structured-header fields are rejected

- **GIVEN** a timestamped-HMAC route has equal timestamp and signature fields or
  a field name containing characters outside the HTTP token grammar
- **WHEN** the operator attempts to persist the route
- **THEN** validation rejects the configuration before persistence

#### Scenario: Undefined numeric verification enum is rejected

- **GIVEN** a route contains a numeric verifier-kind or HMAC-algorithm value not
  defined by the running daemon
- **WHEN** the route catalog validates that definition
- **THEN** the route is invalidated before request handling
- **AND** other valid routes remain available

#### Scenario: New timestamped route uses effective defaults

- **GIVEN** an operator selects timestamped HMAC and supplies a signature header
  and secret without advanced timestamp options
- **WHEN** the route is saved and loaded by the daemon
- **THEN** it uses timestamp field `t`, signature field `v1`, separator `.`, and
  tolerance 300 seconds

#### Scenario: Older daemon encounters a timestamped route

- **GIVEN** a route file explicitly selects the new timestamped-HMAC enum name
- **WHEN** an older daemon that does not recognize that name loads the route
- **THEN** that route fails closed as invalid
- **AND** other webhook routes and the daemon remain available
