# Netclaw 0.18.2 Release Notes

## Features

- **OpenTelemetry telemetry improvements** — Configurable `service.name` and `service.version` in OTEL resource attributes (#1042)

## Fixes

- **CLI** — Skip startup update checks for interactive flows (#1037)
- **Build** — Fix slnx load (#1039), mark Netclaw.Tests.Utilities as non-test project (#1041)

## Refactoring

- **Memory** — Pass 7e: type memory/sub-agent enum fields (#1029)
- **Protocol** — Pass 7d: value objects for ModelId, TurnNumber, et al (#1024)

## Other

- **OpenSpec** — Retire value-object-adoption change (#1040)
