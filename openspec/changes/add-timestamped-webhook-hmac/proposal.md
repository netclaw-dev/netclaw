## Why

PRD-009 permits external services to launch webhook sessions, but the current verifier only accepts body-only HMAC signatures or static secret headers. Providers such as Stripe and TextForge sign `timestamp.rawBody` and require a timestamp tolerance, so Netclaw cannot receive their events without weakening verification outside the daemon.

## What Changes

- Add an opt-in, generic timestamped-HMAC verification kind for structured `t=...,v1=...` signature headers.
- Verify the exact timestamp text and raw request bytes with HMAC-SHA256, accept multiple signatures for secret rotation, and reject deliveries outside a configurable replay window.
- Expose the new mode through route JSON, `netclaw webhooks`, and `set_webhook`, with Stripe-style defaults for field names, separator, and tolerance.
- Preserve body-only `Hmac` as the default and retain `HeaderSecret`; existing route files and callers require no migration.
- Update route schema, operator documentation, runtime skill guidance, and behavioral evals.

In scope for PRD-009 Phase 2 is generic timestamped verification and its configuration surfaces. Provider presets, provider-specific key derivation, a route-authoring TUI, and changes to webhook dispatch, filtering, deduplication, or rate limiting are out of scope.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `inbound-webhooks`: Add explicit timestamped-HMAC verification while preserving existing verification modes and route compatibility.

## Impact

- Configuration: additive enum value and optional fields in the v1 webhook-route schema; no route migration.
- Runtime: one new fail-closed verifier branch using the existing raw request body and injected `TimeProvider`.
- Interfaces: additive CLI flags and optional `set_webhook` arguments; existing invocations remain valid.
- Security: timestamped routes require a valid signature and bounded timestamp; no verifier auto-detection or fallback is introduced.
- Operations: malformed or stale timestamped signatures remain ordinary `401` verification failures and use existing structured logs and counters.
