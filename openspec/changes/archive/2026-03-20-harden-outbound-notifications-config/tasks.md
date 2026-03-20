## 1. Shared notification config validation

- [x] 1.1 Add a shared validator for `NotificationsConfig` and `WebhookTarget`
      covering secure URL rules, loopback HTTP exceptions, fragment rejection,
      and numeric bounds.
- [x] 1.2 Wire the shared validator into daemon startup before
      `WebhookNotificationService` registration so invalid notification config
      fails closed.
- [x] 1.3 Add unit tests for valid HTTPS targets, loopback HTTP exceptions,
      non-loopback HTTP rejection, and out-of-range retry/timeout/dedup values.

## 2. CLI diagnostics and operator guidance

- [x] 2.1 Add a `netclaw doctor` notification-config check that reuses the shared
      validator and emits field-level remediation guidance.
- [x] 2.2 Add doctor coverage for auth-like headers in base config so operators
      are warned to move secrets to `secrets.json` or `NETCLAW_` environment
      variables.
- [x] 2.3 Extend doctor coverage to warn when webhook URLs live in base config
      instead of the secrets overlay.
- [x] 2.3 Update `docs/spec/configuration.md` and related operator guidance with
      a `Notifications` section, secure examples, and secrets-placement notes.
- [x] 2.4 Add doctor tests covering pass, warning-only, and failure outcomes for
      notification config.

## 3. Delivery hardening regression coverage

- [x] 3.1 Ensure notification logs and diagnostics keep target identity visible
      while redacting configured header values.
- [x] 3.2 Preserve best-effort delivery semantics for valid config: record dedup
      only after successful delivery, retry `429` and `5xx` failures, skip other
      4xx retries, warn on queue saturation, and keep the daemon alive when all
      targets fail.
- [x] 3.3 Extend `WebhookNotificationService` tests to cover redaction,
      dedup-after-success behavior, 429 retry behavior, and validated-config
      startup behavior.
- [x] 3.4 Cover streaming-enumeration failure alerts so provider-unreachable and
      failover notifications still fire when async stream consumption fails after
      stream creation.

## 4. Validation

- [x] 4.1 Run `openspec validate --change harden-outbound-notifications-config
      --strict` and resolve any artifact issues before implementation.
