## Context

Netclaw already has a background-service notification path:
`IOperationalNotificationSink` is injected into daemon services, valid alerts are
queued into `WebhookNotificationService`, and delivery is retried with
best-effort semantics. What is missing is the config hardening layer around that
runtime path. Today the `Notifications` section is bound directly from merged
configuration, registered if `Webhooks.Count > 0`, and otherwise left mostly
unspecified.

This change is intentionally config-first, not architecture-first. Actor and
session boundaries stay the same: producers continue to emit alerts through
`IOperationalNotificationSink`, and `WebhookNotificationService` remains a
non-actor hosted service. There is no persistence schema change.

Source PRDs: `PRD-001` (primary), `PRD-002`, `PRD-004`

## Goals / Non-Goals

**Goals:**
- Define a testable config contract for outbound operational notifications.
- Use one shared validator for daemon startup and `netclaw doctor`.
- Keep outbound delivery secure by default with explicit transport rules.
- Preserve best-effort runtime delivery for valid config without letting invalid
  config partially enable the feature.
- Improve operator guidance for secrets placement and remediation.

**Non-Goals:**
- No new delivery channels beyond HTTP webhook POST.
- No change to the alert producer contract or actor message flow.
- No config hot reload for notifications in this slice.
- No per-alert routing, templating, or richer notification formatting.

## Decisions

### Decision: Centralize notification config validation

Add one shared validator for `NotificationsConfig` and `WebhookTarget` that
returns structured field-level issues. Daemon startup and CLI doctor will both
call this validator instead of re-implementing rules in separate places.

Rationale: this avoids drift between preflight checks and live startup behavior,
which is exactly the class of config hardening gap this change is closing.

Alternative considered: keep lightweight startup binding and add doctor-only
validation. Rejected because it still allows invalid config to reach production
runtime and makes failure behavior inconsistent.

### Decision: Secure transport by default, with loopback HTTP exception

Webhook targets must use absolute `https://` URLs, except explicit loopback
development endpoints (`localhost`, `127.0.0.1`, `::1`) which may use
`http://`. Non-loopback plaintext HTTP, unsupported schemes, malformed URLs, and
fragment-bearing URLs are rejected during validation.

Rationale: outbound notifications can carry operational context and often use
bearer-style headers, so plaintext transport should not be the default. The
loopback exception preserves local development and test ergonomics.

Alternative considered: allow any `http://` target because the host is owner
controlled. Rejected because it weakens the default security posture and makes
accidental plaintext egress too easy.

### Decision: Secret placement warning is doctor-driven, not startup-fatal

Webhook URLs and static webhook headers remain supported, but `netclaw doctor`
warns when webhook URLs or auth-like headers such as `Authorization`,
`X-Api-Key`, or `Api-Key` are found in base config. The runtime validator does
not reject these values because the merged configuration graph does not reliably
preserve source origin once config is built.

Rationale: we want actionable operator guidance without false negatives from the
merged runtime view. The doctor command can inspect `netclaw.json` directly and
point operators to `secrets.json` or environment variables.

Alternative considered: make startup reject all static headers. Rejected because
some headers are non-sensitive and because source-aware rejection is unreliable
after configuration merge.

### Decision: Invalid config fails closed before notification service registration

When notification config is invalid, startup should fail before registering
`WebhookNotificationService` or exposing a partially configured notification
surface. Once config passes validation, runtime delivery remains best-effort:
per-target failures are logged, only successful deliveries enter dedup state,
HTTP `429` and `5xx` responses are retried with bounded retries, other `4xx`
responses are not retried, queue saturation drops new alerts with an explicit
warning, and the daemon stays alive.

Rationale: configuration validity is a startup concern, while downstream target
availability is an operational concern. Mixing the two leads to partial enablement
that is hard to reason about.

Alternative considered: silently skip only invalid targets and start the rest.
Rejected because it hides operator mistakes and creates ambiguous production
state.

### Decision: Secret-safe URL display still preserves target identity

Notification logs and operator diagnostics may identify a target, but they do so
using the configured `Name` and a redacted URL display that preserves only the
origin (`scheme://host[:port]`) plus a `/<redacted>` suffix. Full webhook paths,
query strings, fragments, user info, and header values are never shown.

Rationale: many webhook URLs are effectively bearer secrets. Operators still
need enough identity information to distinguish targets, but full path display
would leak secret-bearing endpoints into logs and CLI output.

Alternative considered: display full sanitized URLs without query strings.
Rejected because the path segment itself is often the secret.

## Risks / Trade-offs

- [Risk] Strict HTTPS-by-default rules may reject some currently working lab-only
  endpoints. -> Mitigation: keep the loopback HTTP exception explicit and add
  clear remediation in doctor output.
- [Risk] Doctor-only secret placement warnings may still allow insecure habits if
  operators skip preflight checks. -> Mitigation: document the warning clearly
  and keep runtime logs and CLI output redacted so secrets do not leak even when
  placement is suboptimal.
- [Risk] Shared validation adds upfront work to a path that already “works.” ->
  Mitigation: reuse the same validator across startup, doctor, and tests so the
  maintenance cost stays low.

## Migration Plan

1. Add shared validation types and tests for webhook target shape and numeric
   bounds.
2. Wire startup validation before `WebhookNotificationService` registration so
   invalid config fails closed.
3. Add a doctor check that reuses the validator and emits remediation guidance,
   including warnings for webhook URLs and auth-like headers in base config.
4. Update `docs/spec/configuration.md` with a `Notifications` section and secure
   examples.
5. Validate the OpenSpec change and then implement against the approved tasks.

## Open Questions

- Should MVP cap the maximum number of webhook targets, or is bounded retry and
  timeout tuning sufficient for now?
- Should future notification hot reload reuse the same validator, or should it
  remain startup-only until a broader config-reload slice is planned?
