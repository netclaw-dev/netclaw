## MODIFIED Requirements

### Requirement: Attachment file taxonomy and inline decisions

The system SHALL use the same canonical file taxonomy for chat attachment ingress
and local file inspection: `Image`, `Pdf`, `Document`, `Archive`, `Media`, and
`Other`. Inline decisions SHALL be shared so images are inlined only when image
input is available, PDFs remain path-only unless native provider support is
explicitly added, and all other non-image formats are path-only.

#### Scenario: Chat attachment and file_read agree on image modality gap

- **GIVEN** a PNG file
- **AND** the active model does not support image input
- **WHEN** the file arrives as a chat attachment or is inspected by `file_read`
- **THEN** both paths use the canonical image modality-gap note

#### Scenario: PDF remains path-only

- **GIVEN** a PDF file
- **WHEN** the file arrives as a chat attachment or is inspected by `file_read`
- **THEN** the file is not emitted as `DataContent`
- **AND** the agent receives explicit guidance that native PDF content is not
  available through the current path
