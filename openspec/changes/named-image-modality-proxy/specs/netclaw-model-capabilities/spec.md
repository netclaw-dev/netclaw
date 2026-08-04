## ADDED Requirements

### Requirement: Proxy-backed image compatibility

An image in active session history SHALL be compatible with a text-only main model only when a valid durable proxy result exists or a configured image proxy can create one.
The actor SHALL create all required results before the main model call.
It SHALL preserve the original media reference.

#### Scenario: Durable result satisfies text-only input

- **GIVEN** active session history contains an image and its durable proxy result
- **AND** the main model accepts text only
- **WHEN** the actor checks input compatibility
- **THEN** it SHALL treat the image as proxy-backed text input
- **AND** it SHALL preserve the original image reference

#### Scenario: Configured proxy can repair historical input

- **GIVEN** active session history contains an image without a durable result
- **AND** a valid image proxy is configured
- **WHEN** the actor checks input compatibility
- **THEN** it SHALL request lazy proxy analysis
- **AND** it SHALL defer the main model call until the result is durable

#### Scenario: No proxy keeps the compatibility failure

- **GIVEN** active session history contains an image without a durable result
- **AND** the main model accepts text only
- **AND** no image proxy is configured
- **WHEN** the actor checks input compatibility
- **THEN** it SHALL emit the standard input compatibility error
- **AND** no model client SHALL receive a request
