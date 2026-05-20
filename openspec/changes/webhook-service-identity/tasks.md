## 1. Remove Netclaw-owned identity config

- [x] 1.1 Remove `ServiceName` / `ServiceNamespace` / `ServiceInstanceId` from `TelemetryOptions`
- [x] 1.2 Remove those properties from the `Telemetry` block of `netclaw-config.v1.schema.json`

## 2. Resolve identity from the OpenTelemetry resource

- [x] 2.1 Add `record ServiceIdentity(string Name, string? Namespace, string? InstanceId, string Version)` in `Netclaw.Configuration`
- [x] 2.2 Add `NetclawResourceDetector : IResourceDetector` supplying assembly/runtime defaults (`service.name=netclawd`, `service.instance.id={hostname}:{processId}`, `service.version`)
- [x] 2.3 In `AddNetclawTelemetry`, build the `Resource` from `CreateEmpty().AddDetector(NetclawResourceDetector).AddTelemetrySdk().AddEnvironmentVariableDetector()` (env detector last so env wins) and feed its attributes to the logging and metrics pipelines
- [x] 2.4 Add `ProjectServiceIdentity(Resource)`, register the projected `ServiceIdentity` as a DI singleton before the telemetry-enabled early-out, and log it once at startup via `ServiceIdentityStartupLogger`

## 3. Webhook payload stamping

- [x] 3.1 Inject `ServiceIdentity` into `WebhookNotificationService`; add a nested `service` object to the generic payload
- [x] 3.2 Pass `ServiceIdentity` to `SlackWebhookPayloadBuilder.Build`; add a `Service` field and namespace/instance/version context elements, omitting namespace and instance id when absent

## 4. Tests

- [x] 4.1 Add `ServiceIdentityProjectionTests` covering resource-to-identity projection (all attributes, optional fields absent, name fallback)
- [x] 4.2 Update webhook/Slack payload tests; cover the namespace/instance-absent case

## 5. Docs

- [x] 5.1 Update the Telemetry section of `docs/spec/configuration.md` to document env-var-based identity
