## Context

Webhook routes are secret-bearing JSON files loaded independently at request time. `WebhookRequestVerifier` currently verifies either HMAC-SHA256 over the raw body or a static secret header. The endpoint already retains the raw bytes, and the daemon already registers `TimeProvider`, so timestamped verification can reuse both seams without changing actor messages, persistence, dispatch, filtering, deduplication, or rate limiting.

Compatibility includes existing files, CLI/tool callers, and downgrade behavior. A new daemon must not reinterpret an old route; an old daemon encountering a route that explicitly selects the new kind must fail that route closed without affecting other routes.

## Goals / Non-Goals

**Goals:**

- Verify Stripe-style `t=...,v1=...` signatures over the exact `timestamp.separator.rawBody` bytes.
- Enforce a bounded replay window using injectable time.
- Support multiple signature values for sender-side secret rotation.
- Keep existing verification behavior, defaults, and route files unchanged.
- Give operators generic CLI/tool controls plus copyable Stripe and TextForge configurations.

**Non-Goals:**

- Provider-specific verifier types, key derivation, or automatic provider detection.
- Deprecating body-only HMAC or static header secrets.
- Persisting replay state beyond the existing delivery-ID deduplication behavior.
- Changing webhook sessions, endpoint responses, or ingress ordering.

## Decisions

1. **Add an explicit discriminator rather than auto-detection.** `HmacTimestamped` is appended to the enum. `Hmac` remains the default because senders use incompatible protocols. Auto-detection or fallback could accept a request under a weaker mode after the intended mode failed.

2. **Keep raw configuration optional and resolve effective defaults at runtime.** Nullable `ToleranceSeconds`, `TimestampField`, `SignatureField`, and `SignedPayloadSeparator` fields preserve legacy JSON. Their effective values for the new kind are `300`, `t`, `v1`, and `.`. Null fields are omitted when writing routes. The v1 route schema gains only optional properties and the additive enum value.

3. **Parse strictly and sign exact received bytes.** The parser accepts comma-separated `key=value` components, requires exactly one timestamp and at least one signature, and rejects malformed or ambiguous input. The numeric timestamp is used for tolerance checks, while its original text is used in the signed payload. Signature comparison decodes 32-byte SHA-256 hex values and uses fixed-time equality.

4. **Use the existing `TimeProvider` registration.** `WebhookRequestVerifier` requires `TimeProvider` through DI. Tests use `FakeTimeProvider`; production uses `TimeProvider.System` already registered by the daemon.

5. **Keep configuration generic at the user surface.** The CLI adds `hmac-timestamped` plus advanced optional flags. Providers still specify `SignatureHeaderName` because Stripe and TextForge use different names. Provider presets are deferred until repeated configuration demonstrates a need.

6. **Preserve inactive and omitted fields.** Validation applies timestamp constraints only when `HmacTimestamped` is selected. Switching kinds does not erase dormant settings, and old kinds do not acquire new behavior. CLI and `set_webhook` updates retain optional route and verification values that the caller omits; `set_webhook` performs its read, audience authorization, patch, validation, and write under one store lock.

7. **Reject unrepresentable structured-header field names before persistence.** Effective timestamp and signature field names must be distinct HTTP tokens. This excludes whitespace, delimiters, non-ASCII characters, and controls that cannot form a valid structured-header key.

8. **Reject undefined numeric enum values during shared validation.** Route deserialization retains its prior ability to read numeric enum values for compatibility, but values outside the defined verifier-kind and HMAC-algorithm sets fail route validation before request handling.

No actor or persistence boundary changes. Verification still returns the existing in-memory result consumed by the endpoint before any session actor is created.

## Risks / Trade-offs

- **Clock skew rejects legitimate events** → use a documented 300-second default, configurable from 1 through 3600 seconds, and expose a distinct internal rejection reason.
- **Structured header ambiguity** → reject duplicate timestamps, missing values, malformed pairs, invalid Unix timestamps, and invalid signature hex.
- **CLI output consumers break on additive fields** → emit timestamp-specific fields only for the new kind and preserve old-kind output shape.
- **Downgrade encounters the new enum** → older daemons fail that route during parsing; the route catalog removes it and emits its existing invalid-route alert.
- **Configuration files accumulate irrelevant fields** → omit nullable timestamp fields from serialization and ignore dormant values for other verifier kinds.

## Migration Plan

No migration runs. Existing routes continue to deserialize with null timestamp settings and retain their existing discriminator. Operators opt in by changing or creating a route with `HmacTimestamped`. Rollback requires changing such routes back to a verifier supported by the older daemon before downgrading; otherwise only those routes remain unavailable.

## Open Questions

None.
