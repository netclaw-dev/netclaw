## 1. Configuration Contract

- [x] 1.1 Add the timestamped-HMAC enum and optional settings with effective defaults that preserve legacy routes
- [x] 1.2 Extend the v1 webhook-route schema and shared validation for the new kind
- [x] 1.3 Add legacy load, round-trip, schema, and invalid-config coverage

## 2. Runtime Verification

- [x] 2.1 Implement strict structured-header parsing, raw-payload HMAC verification, rotation signatures, and replay tolerance using `TimeProvider`
- [x] 2.2 Add verifier and endpoint tests for valid, boundary, malformed, stale, future, and multiple-signature deliveries
- [x] 2.3 Prove existing HMAC and header-secret runtime behavior remains unchanged

## 3. Operator Surfaces

- [x] 3.1 Add mode parsing, timestamp flags, mode-specific display, validation, and help to `netclaw webhooks`
- [x] 3.2 Add trailing optional timestamp arguments and validation to `set_webhook`
- [x] 3.3 Add CLI and tool tests for new and legacy invocations

## 4. Documentation and Guidance

- [x] 4.1 Update engineering configuration docs and the inbound-webhooks OpenSpec main capability after verification
- [x] 4.2 Update and version the `netclaw-operations` webhook guidance with mode selection and examples
- [x] 4.3 Update behavioral eval cases for the changed tool schema and skill guidance

## 5. Verification and External Documentation

- [x] 5.1 Run targeted tests, full test suite, evals, Slopwatch, header verification, and diff checks
- [x] 5.2 Verify implementation against OpenSpec artifacts and sync the delta spec
- [x] 5.3 File scoped configuration and CLI documentation issues in `netclaw-dev/netclaw-website`
