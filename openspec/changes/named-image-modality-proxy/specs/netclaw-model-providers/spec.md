## ADDED Requirements

### Requirement: Named image proxy assignment

Named model configuration SHALL support an optional `Models.Proxies.Image` string that references one existing `Models.Definitions` entry.
Startup SHALL reject an unknown reference.
Startup SHALL reject a proxy model that lacks image input or text output.

#### Scenario: Valid image proxy starts

- **GIVEN** `Models.Proxies.Image` references a definition with image input and text output
- **WHEN** the daemon validates model configuration
- **THEN** startup SHALL succeed
- **AND** the runtime registry SHALL expose that definition to the image proxy service

#### Scenario: Unknown image proxy blocks startup

- **GIVEN** `Models.Proxies.Image` references no configured definition
- **WHEN** the daemon validates model configuration
- **THEN** startup SHALL fail with an error that identifies the reference

#### Scenario: Incompatible proxy capabilities block startup

- **GIVEN** `Models.Proxies.Image` references a model without image input or text output
- **WHEN** the daemon resolves effective model capabilities
- **THEN** startup SHALL fail with a model capability error
