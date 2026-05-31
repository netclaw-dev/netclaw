## ADDED Requirements

### Requirement: Attachment ingress uses catalog-backed media classification

Channel attachment ingress SHALL classify inbound files through the shared media
catalog. Broad MIME prefixes such as `image/`, `audio/`, and `video/` SHALL NOT
grant attachment categories unless the concrete MIME type is explicitly present
in the catalog.

#### Scenario: Public image attachment uses explicit catalog support

- **GIVEN** a Public session receives an attachment declared as `image/png`
- **AND** the media catalog classifies `image/png` as `Image`
- **WHEN** channel attachment policy is evaluated before download
- **THEN** the Public profile can allow the attachment category

#### Scenario: Unknown media subtype does not bypass Team policy

- **GIVEN** a Team session receives an attachment declared as `video/x-unknown`
- **WHEN** channel attachment policy is evaluated before download
- **THEN** the attachment is not accepted as `Media` by prefix alone
- **AND** it is rejected unless the catalog explicitly supports that MIME type

### Requirement: Attachment ingress uses verified MIME after content scanning

Channel attachment ingress SHALL keep declared transport MIME separate from
scanner-verified MIME. Accepted attachment announcements, inline `DataContent`,
session media references, and logs that describe delivered file type SHALL use
the verified canonical MIME returned by the scanner. Declared MIME MAY be logged
as source metadata.

#### Scenario: Octet-stream PNG is delivered as verified PNG

- **GIVEN** a channel attachment is declared as `application/octet-stream`
- **AND** its filename and bytes validate as PNG
- **WHEN** the attachment is accepted
- **THEN** the attachment announcement uses `image/png`
- **AND** any inlined `DataContent` uses `image/png`

#### Scenario: Spoofed image is rejected before delivery

- **GIVEN** a channel attachment is declared as `image/png`
- **AND** its bytes contain an executable signature
- **WHEN** the content scanner evaluates the file
- **THEN** the attachment is rejected
- **AND** no `DataContent` or session media reference is produced
