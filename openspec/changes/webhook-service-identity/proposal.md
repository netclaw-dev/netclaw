## Why

Netclaw's operational webhook alerts (daemon crash, provider failover, MCP
disconnect, etc.) carry only `Hostname` as an instance identifier. When several
Netclaw instances post alerts to the same Slack channel — or run co-located on
one host — every alert looks identical and an operator cannot tell which agent
is failing.

Every other part of Netclaw already carries a service identity for free via the
OpenTelemetry infrastructure. Operational webhook alerts should too — sourced
from the same OpenTelemetry resource and configured the standard OpenTelemetry
way, consistent with how every other Petabridge service is set up.

## What Changes

- Build the OpenTelemetry `Resource` once at startup and project its
  `service.*` attributes (`service.name`, `service.namespace`,
  `service.instance.id`, `service.version`) into a `ServiceIdentity` shared with
  the operational webhook payload builders.
- Service identity is resolved purely from the standard OpenTelemetry
  environment variables (`OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`) via the
  OpenTelemetry resource detectors — the same mechanism as every other
  OpenTelemetry service. No Netclaw-owned identity config.
- Operational webhook payloads carry the service identity:
  - Generic JSON gains a nested `service` object (additive).
  - Slack Block Kit gains a `Service` field plus namespace/instance/version in
    the context footer.
- **Removes** the `Telemetry:ServiceName` config property added in #1042 and
  does not introduce `ServiceNamespace`/`ServiceInstanceId` config — service
  identity is no longer Netclaw-owned configuration.

Not breaking: webhook payload changes are additive; the removed `ServiceName`
config knob has no released consumers.

## Capabilities

### New Capabilities

- `service-identity`: how Netclaw resolves a per-instance service identity from
  the OpenTelemetry resource and surfaces it on its operational-alert webhook
  payloads.

### Modified Capabilities

<!-- None. No existing spec's requirements change. -->

## Impact

- PRD: operational observability concern under `PRD-001-netclaw-mvp`.
- Config: removes identity properties from `TelemetryOptions` and the `Telemetry`
  block of `netclaw-config.v1.schema.json`.
- Code: new `Netclaw.Configuration/ServiceIdentity.cs`;
  `TelemetryRegistrationExtensions` (resource build + projection);
  `WebhookNotificationService`; `Netclaw.Channels.Slack/Webhooks/SlackWebhookPayloadBuilder`.
- Tests: `WebhookNotificationServiceTests`, `SlackBlockKitWebhookFormatterTests`,
  new `ServiceIdentityProjectionTests`.
- Docs: `docs/spec/configuration.md`. User-facing website docs
  (`netclaw-dev/netclaw-website`) are tracked by a separate GitHub issue.
- Operational: webhook consumers see new (additive) payload fields; operators
  running multiple instances set `OTEL_SERVICE_NAME` (or `OTEL_RESOURCE_ATTRIBUTES`).
