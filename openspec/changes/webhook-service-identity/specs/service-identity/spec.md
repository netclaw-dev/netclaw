## ADDED Requirements

### Requirement: Service identity from the OpenTelemetry resource

Netclaw SHALL resolve a service identity by projecting the `service.*`
attributes of the OpenTelemetry resource built at daemon startup. `service.name`,
`service.namespace`, and `service.instance.id` SHALL be sourced from the
standard OpenTelemetry environment variables (`OTEL_SERVICE_NAME`,
`OTEL_RESOURCE_ATTRIBUTES`). When the environment does not supply them, Netclaw
SHALL apply assembly- and runtime-derived defaults through an `IResourceDetector`
registered before the environment-variable detector: `service.name` defaults to
`netclawd`, `service.instance.id` defaults to `{hostname}:{processId}`, and
`service.version` is the running netclaw build version. Netclaw SHALL NOT provide
its own configuration properties for service identity.

#### Scenario: Service name from the environment

- **WHEN** `OTEL_SERVICE_NAME` (or `OTEL_RESOURCE_ATTRIBUTES` carrying
  `service.name`) is set
- **THEN** the resolved service name is that value

#### Scenario: Service name defaulted when absent from the environment

- **WHEN** neither `OTEL_SERVICE_NAME` nor `OTEL_RESOURCE_ATTRIBUTES` supplies a
  service name
- **THEN** the resolved service name is `netclawd`

#### Scenario: Instance id defaulted from the runtime

- **WHEN** `OTEL_RESOURCE_ATTRIBUTES` does not supply `service.instance.id`
- **THEN** the resolved instance id is `{hostname}:{processId}`

#### Scenario: Namespace is optional

- **WHEN** `OTEL_RESOURCE_ATTRIBUTES` does not supply `service.namespace`
- **THEN** the resolved identity has no namespace

### Requirement: Service identity on the OpenTelemetry pipelines

The OpenTelemetry logging and metrics pipelines SHALL carry the same resolved
resource attributes that are stamped onto operational webhook alerts, so
telemetry and alerts from the same instance agree. This applies when telemetry
export is enabled.

#### Scenario: Pipelines and webhooks agree

- **WHEN** telemetry export is enabled
- **THEN** the `service.*` attributes on the OpenTelemetry logging and metrics
  resources match the identity stamped on operational webhook alerts

### Requirement: Service identity on operational webhook alerts

Operational webhook alert payloads SHALL carry the resolved service identity.
Identity SHALL be stamped even when telemetry export is disabled.

The generic JSON payload SHALL include a nested `service` object with `name` and
`version`, and `namespace` / `instanceId` when present. Existing payload fields
SHALL remain unchanged. The Slack Block Kit payload SHALL include the service
name as a labeled field and the version, plus the namespace and instance id when
present.

#### Scenario: Generic JSON payload carries service identity

- **WHEN** an operational alert is delivered to a `Generic`-format webhook target
- **THEN** the payload contains a `service` object with `name` and `version`
- **AND** the pre-existing fields (`alertId`, `type`, `severity`, `summary`,
  `timestamp`, `source`, `hostname`, `context`) are unchanged

#### Scenario: Slack payload carries service identity

- **WHEN** an operational alert is delivered to a `Slack`-format webhook target
- **THEN** the Slack message shows the service name as a labeled field and the
  version in the context footer

#### Scenario: Identity stamped with telemetry export disabled

- **WHEN** `Telemetry:Enabled` is false
- **AND** an operational alert is delivered to any webhook target
- **THEN** the payload still carries the resolved service identity

#### Scenario: Optional fields omitted when absent

- **WHEN** the resolved identity has no namespace (or no instance id)
- **THEN** that field is omitted from both the generic and Slack payloads
