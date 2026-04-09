# netclaw-provider-auth Delta Spec

## ADDED Requirements

### Requirement: Explicit provider auth lifecycle stages
The system SHALL model provider authentication as four explicit lifecycle responsibilities: token acquisition, token refresh, token persistence, and token-to-runtime-client mapping. Provider auth implementations SHALL compose these responsibilities without collapsing them into a single opaque runtime path.

#### Scenario: API-key provider uses explicit mapping stage
- **WHEN** a provider is configured for API-key authentication
- **THEN** runtime client construction resolves through the token-to-runtime-client mapping stage
- **AND** the auth flow does not require token refresh behavior for a valid API-key configuration

#### Scenario: OAuth-backed provider uses explicit lifecycle stages
- **WHEN** a provider is configured for OAuth or subscription-backed authentication
- **THEN** runtime activation resolves acquisition, refresh, persistence, and runtime-client mapping through explicit auth stages
- **AND** each stage can report stage-specific validation or runtime failures

### Requirement: OpenAI is the first protected provider-auth implementation
OpenAI SHALL be the first implementation of the provider-auth seam and SHALL preserve compatibility for both API-key and OAuth/subscription-backed paths during early extraction phases.

#### Scenario: OpenAI API-key auth remains valid through extracted auth seam
- **WHEN** provider-auth extraction is introduced and OpenAI is configured with a valid API key
- **THEN** the OpenAI runtime client is created successfully through the extracted auth seam
- **AND** successful inference behavior remains equivalent to the pre-extraction path

#### Scenario: OpenAI OAuth or subscription auth remains valid through extracted auth seam
- **WHEN** provider-auth extraction is introduced and OpenAI is configured with valid OAuth or subscription-backed credentials
- **THEN** the OpenAI runtime client is created successfully through the extracted auth seam
- **AND** successful runtime behavior remains equivalent to the pre-extraction path

### Requirement: Provider auth validation fails closed
Provider auth validation SHALL fail closed across schema, doctor, startup, and hot reload. Invalid or partial auth state SHALL NOT silently downgrade to another auth mode or to anonymous runtime behavior.

#### Scenario: Partial OAuth configuration is rejected
- **WHEN** provider auth configuration declares an OAuth-backed mode but omits a required auth stage input
- **THEN** validation fails with explicit stage-specific remediation
- **AND** runtime activation is blocked for that provider configuration

#### Scenario: Invalid refresh state does not fall back to stale runtime client
- **WHEN** runtime auth refresh fails for a provider requiring refreshable credentials
- **THEN** the failure is surfaced as an auth-specific runtime error
- **AND** the system does not silently substitute another auth mode or stale fallback client
