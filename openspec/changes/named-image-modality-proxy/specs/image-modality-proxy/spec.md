## ADDED Requirements

### Requirement: Durable image proxy analysis

When the main model lacks image input and `Models.Proxies.Image` is configured, the session actor SHALL create a durable text description for each image that lacks one.
The proxy request SHALL contain one image and one fixed, versioned, OCR-aware prompt.
The proxy request SHALL contain no session history and no tools.

#### Scenario: New image receives one proxy analysis

- **GIVEN** the main model accepts text only
- **AND** a valid image proxy is configured
- **AND** a new session image has no durable analysis
- **WHEN** the actor prepares the main model call
- **THEN** the proxy SHALL receive the image and fixed prompt once
- **AND** the actor SHALL persist the result before it calls the main model

#### Scenario: Empty proxy result stops the turn

- **GIVEN** an image requires proxy analysis
- **WHEN** the proxy returns empty text or fails
- **THEN** the actor SHALL emit a visible proxy error
- **AND** the main model SHALL NOT receive a request

### Requirement: Durable proxy result identity

Each proxy result SHALL record the source media path, proxy definition name, proxy model ID, prompt version, description, and UTC timestamp.
The session snapshot SHALL preserve the same data.

#### Scenario: Recovery reuses a saved result

- **GIVEN** a session persisted an image proxy result
- **WHEN** the actor recovers and prepares the same image for a text-only main model
- **THEN** it SHALL reuse the saved description
- **AND** it SHALL NOT call the proxy again

#### Scenario: Historical image receives lazy analysis

- **GIVEN** recovered history contains an image without a proxy result
- **AND** the main model accepts text only
- **AND** a valid image proxy is configured
- **WHEN** the user resumes the session
- **THEN** the actor SHALL create and persist the result before the main model call

### Requirement: Original image remains authoritative

The system SHALL preserve each original media reference and image file after proxy analysis.
The message assembler SHALL select the original image when the main model accepts images.
It SHALL select the durable description when the main model accepts text only.

#### Scenario: Main model changes back to image support

- **GIVEN** a session image has a durable proxy result
- **AND** the active main model accepts image input
- **WHEN** the actor assembles session history
- **THEN** the main model SHALL receive the original image
- **AND** it SHALL NOT receive the proxy description for that image

### Requirement: Proxy output is untrusted content

The assembler SHALL label each proxy description as untrusted user content and include its session-relative image path.
It SHALL neutralize the fixed wrapper delimiter if the proxy emits that delimiter.

#### Scenario: Proxy output contains the wrapper delimiter

- **GIVEN** a proxy result contains the fixed wrapper end marker
- **WHEN** the actor prepares the derived text
- **THEN** it SHALL neutralize that marker
- **AND** the result SHALL remain inside one untrusted-content wrapper
