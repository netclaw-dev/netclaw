## ADDED Requirements

### Requirement: Slack file attachment download

The Slack adapter SHALL download file attachments from Slack messages and
write them to the session-scoped media directory. Only supported image MIME
types SHALL be downloaded. The bot token SHALL be used for authenticated
download.

#### Scenario: Image attachment downloaded

- **GIVEN** a Slack message event includes a `files` array with an image
  attachment (MIME type `image/png`, `image/jpeg`, `image/gif`, or
  `image/webp`)
- **WHEN** the Slack adapter processes the event
- **THEN** the adapter SHALL download the file using the bot token for
  authentication
- **AND** write the file to the session media directory
- **AND** include a media file reference in the `ChannelInput`

#### Scenario: Unsupported file type skipped

- **GIVEN** a Slack message event includes a file attachment with an
  unsupported MIME type (e.g., `application/pdf`)
- **WHEN** the Slack adapter processes the event
- **THEN** the adapter SHALL skip the file
- **AND** log a debug message indicating the unsupported file type

#### Scenario: File download failure handled

- **GIVEN** a Slack message event includes a supported image attachment
- **AND** the file download fails (timeout, auth error, or 404)
- **WHEN** the Slack adapter processes the event
- **THEN** the adapter SHALL log a warning with the failure reason
- **AND** skip the failed file
- **AND** continue processing the text portion of the message

### Requirement: Slack file upload for outbound media

The Slack adapter SHALL upload files to Slack threads when it receives
`FileOutput` events from the session broadcast.

#### Scenario: FileOutput triggers Slack upload

- **GIVEN** the Slack adapter receives a `FileOutput` event
- **WHEN** the adapter processes the event
- **THEN** the adapter SHALL call `files.uploadV2` to attach the file to the
  originating Slack thread
- **AND** use the bot token for authentication

#### Scenario: Upload failure logged

- **GIVEN** the Slack adapter receives a `FileOutput` event
- **AND** the file upload to Slack fails
- **WHEN** the adapter processes the event
- **THEN** the adapter SHALL log a warning with the failure reason
- **AND** the session SHALL continue normally
