## ADDED Requirements

### Requirement: Attachment announcements identify the authoritative inbox path

Accepted channel attachments SHALL be announced with the final collision-safe `inbox/...` path returned by the inbox writer. The path SHALL be relative to `session_dir`, SHALL resolve to the accepted upload on disk, and SHALL remain independent of any opaque filename used for internal model-media persistence.

#### Scenario: Live same-name attachments announce distinct existing paths

- **GIVEN** two live attachments named `image.png` arrive in the same session
- **WHEN** the shared ingress pipeline accepts both files
- **THEN** their announcements contain distinct collision-safe paths such as `inbox/image.png` and `inbox/image_1.png`
- **AND** each announced path resolves under `session_dir` to its accepted file

#### Scenario: Historical attachment announces its stable inbox path

- **GIVEN** a historical attachment is promoted under a deterministic `image_hist_<suffix>.png` filename
- **WHEN** the history fetcher constructs accepted attachment contents
- **THEN** the announcement path contains that final inbox filename
- **AND** the announced path resolves under `session_dir` to the promoted file

#### Scenario: Inline persistence filename remains internal

- **GIVEN** an accepted image is both stored in the inbox and copied to internal model media
- **WHEN** the attachment announcement is delivered to the agent
- **THEN** its path identifies the collision-safe inbox file
- **AND** the opaque internal media filename is not included in the announcement

#### Scenario: Supported chat adapters share the path contract

- **WHEN** Slack, Discord, or Mattermost accepts a live or historical attachment
- **THEN** the adapter uses the shared authoritative inbox-path announcement contract
