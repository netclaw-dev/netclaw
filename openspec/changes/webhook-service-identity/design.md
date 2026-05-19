## Context

Change #1042 made the OpenTelemetry `service.name` configurable via a
Netclaw-owned `Telemetry:ServiceName` config property. That diverges from how
every other Petabridge service (e.g. Sdkbin) configures OpenTelemetry — those
rely on the standard `OTEL_SERVICE_NAME` / `OTEL_RESOURCE_ATTRIBUTES` environment
variables via the OpenTelemetry resource detectors, with no app-owned config.

The operational webhook surface (`WebhookNotificationService` +
`SlackWebhookPayloadBuilder`) needs the service identity as plain data to stamp
into payloads, so it cannot rely solely on the OpenTelemetry SDK's internal
resource — it needs a projected value object.

Constraints:
- Assembly dependency direction is `Netclaw.Daemon` → `Netclaw.Channels.Slack` →
  `Netclaw.Configuration`. A type shared by the daemon and the Slack builder
  must live in `Netclaw.Configuration`.
- Webhook alerts must carry identity even when OTLP export is disabled.

## Goals / Non-Goals

**Goals:**
- One service identity, sourced from the OpenTelemetry resource, shared by the
  OTel pipelines and both webhook payload formats.
- Configure identity the standard OpenTelemetry way — environment variables
  only, no Netclaw-owned config.

**Non-Goals:**
- No Netclaw identity config knobs.
- No OTel `host.*` attributes — the existing `Hostname` field is retained.
- No website (`netclaw-dev/netclaw-website`) doc edits — tracked separately.
- Agent-name fallback for `service.name` (using the init-wizard agent name) —
  deferred; the agent name is not currently persisted as structured config.

## Decisions

**Decision: a `ServiceIdentity` record in `Netclaw.Configuration`.** It lives in
the lowest shared assembly so both `Netclaw.Daemon` and `Netclaw.Channels.Slack`
can consume it. `Namespace` and `InstanceId` are nullable — absent unless the
environment supplies them.

**Decision: build the OpenTelemetry `Resource` once, project a `ServiceIdentity`
from it.** `AddNetclawTelemetry` builds the resource via
`ResourceBuilder.CreateDefault()` — which includes the OTel env-var detectors
for `OTEL_SERVICE_NAME` and `OTEL_RESOURCE_ATTRIBUTES` — plus `service.version`
from `BuildInfo`. `ProjectServiceIdentity` reads the `service.*` attributes into
a `ServiceIdentity`, registered as a DI singleton before the `Enabled`
early-out so webhook alerts carry identity even when export is off. The OTel
logging/metrics pipelines are fed the same resource's attributes, so telemetry
and webhooks agree.

**Decision: environment variables are the primary source; defaults via an
`IResourceDetector`.** Identity comes from the standard OpenTelemetry environment
variables. Netclaw does not own identity config and does not reimplement env-var
precedence — the OTel SDK detectors do that. When the environment supplies
nothing, a `NetclawResourceDetector : IResourceDetector` contributes
assembly/runtime defaults (`service.name=netclawd`,
`service.instance.id={hostname}:{processId}`, `service.version` from `BuildInfo`).
`ResourceBuilder.Build()` merges detectors in registration order and a later
detector wins on key collision (verified by decompiling OpenTelemetry 1.15.3), so
the Netclaw detector is registered **before** `AddEnvironmentVariableDetector()` —
env vars override the defaults. Using `IResourceDetector` is the standard
OpenTelemetry attribute-contribution API and matches the Petabridge house
pattern.

**Decision: log the resolved identity at startup.** A `ServiceIdentityStartupLogger`
hosted service logs the resolved `ServiceIdentity` once, so an operator can
confirm what identity the instance reports (and whether their `OTEL_*` env vars
took effect) from the daemon log.

**Decision: additive payload changes.** Generic JSON gains a nested `service`
object; Slack gains a `Service` field and context-footer elements. Namespace and
instance id are omitted when absent. No consumer breaks.

## Risks / Trade-offs

- [With no env vars set, `service.name` is OpenTelemetry's `unknown_service:*`
  default and co-located instances are indistinguishable] → Acceptable and
  OTel-standard: operators running multiple instances set `OTEL_SERVICE_NAME`,
  exactly as they would for any other service.
- [Calling `.AddService(...)` would shadow env-detected attributes] →
  Mitigation: do not call `.AddService(...)`; add only `service.version` as an
  attribute, leaving identity resolution to the env detectors.

## Migration Plan

Removes the `Telemetry:ServiceName` property added in #1042. It has no released
consumers. A config that still sets it would be flagged by `ConfigSchemaDoctorCheck`
(the `Telemetry` block is `additionalProperties: false`); `netclaw doctor --fix`
removes the now-unknown property automatically. No data migration.

## Open Questions

None.
