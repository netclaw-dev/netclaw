## Why

Source PRDs: `PRD-001` (primary), `PRD-002`, `PRD-004`

Netclaw already emits outbound operational alerts through webhook targets, but the
`Notifications` configuration has no explicit spec contract, no shared startup
validation, and no doctor coverage. A malformed target URL, unsafe retry/timeout
value, or misplaced secret header can silently weaken alert delivery or leak
credentials, so the config path needs to fail closed before more runtime paths
depend on it.

## What Changes

- Define the MVP contract for outbound operational notification webhooks,
  including target shape, secure transport rules, and bounded delivery tuning.
- Add shared validation for `NotificationsConfig` and `WebhookTarget` so daemon
  startup and `netclaw doctor` enforce the same rules and remediation paths.
- Require secure webhook URLs by default, allowing plaintext HTTP only for
  explicit loopback/local-development endpoints.
- Add secret-handling guidance and redaction expectations for webhook URLs and
  static headers so sensitive values do not appear in logs or diagnostics.
- Update operator documentation with a `Notifications` configuration reference
  and secure examples.

## Capabilities

### New Capabilities

- `netclaw-operational-notifications`: Outbound operational alert delivery over
  configured webhook targets, including secure target validation, bounded retry
  settings, and best-effort delivery isolation.

### Modified Capabilities

- `netclaw-cli`: `netclaw doctor` validates outbound notification config,
  surfaces field-level remediation, and warns when auth-like headers are placed
  in base config instead of the secrets overlay.

## In Scope (MVP)

- Webhook target validation for URL scheme, loopback-only HTTP exceptions, and
  bounded timeout/retry/dedup settings.
- Shared validation used by daemon startup and CLI doctor.
- Redacted diagnostics and operator docs for secret-bearing webhook URLs and
  headers.

## Out of Scope

- New delivery backends such as Slack, email, or SMS.
- Per-alert routing rules, templating, or rich notification formatting.
- Notifications config hot reload.

## Impact

- **Code/Runtime**: `NotificationsConfig`, `WebhookTarget`, daemon startup
  registration, `WebhookNotificationService`, and CLI doctor checks.
- **Security**: preserves secure-by-default outbound delivery, avoids plaintext
  non-loopback webhook targets, and reduces accidental credential exposure in
  logs, diagnostics, or `netclaw.json`.
- **Operations**: gives operators preflight diagnostics and documented config
  examples instead of discovering notification failures only after an incident.
- **Docs**: updates `docs/spec/configuration.md` and related operator guidance to
  describe the `Notifications` section.
