## MODIFIED Requirements

### Requirement: Skill content scanner interface

The system SHALL define an `ISkillContentScanner` interface for validating
skill content before writes and feed ingestion. The default production
implementation SHALL enforce a deterministic skill content policy; it SHALL NOT
default to a no-op allow-all scanner in runtime service registration.

#### Scenario: Managed skill write uses enforced scanner

- **WHEN** `skill_manage(action: "create")` or `skill_manage(action: "edit")` is called
- **THEN** `ISkillContentScanner.ScanAsync()` is invoked with the candidate `SKILL.md` content
- **AND** the write proceeds only if the scanner returns `IsAllowed = true`

#### Scenario: Prompt-bearing text is rejected on high-risk injection finding

- **GIVEN** a `SKILL.md` or reference document contains content classified as `PromptInjectionRisk.High`
- **WHEN** the skill content scanner evaluates the file
- **THEN** the scanner returns `IsAllowed = false`
- **AND** the rejection reason identifies prompt-injection risk

#### Scenario: Binary resource is rejected

- **GIVEN** a skill resource file contains binary or executable payload bytes
- **WHEN** the skill content scanner evaluates the file
- **THEN** the scanner returns `IsAllowed = false`
- **AND** the rejection reason identifies unsupported content

#### Scenario: Scanner failure rejects the candidate content

- **WHEN** the skill content scanner encounters an internal failure while evaluating candidate content
- **THEN** the result is `IsAllowed = false`
- **AND** the rejection reason states that scanning failed rather than silently allowing the content

## ADDED Requirements

### Requirement: System skill sync enforces content scanning
System skill feed sync SHALL scan downloaded `SKILL.md` files and resource files
before replacing the installed on-disk skill version. If any file in the synced
candidate version fails scanning, the new version SHALL be rejected and the
previous accepted version SHALL remain installed.

#### Scenario: Reject synced skill version with unsafe main content
- **GIVEN** a signed system skill update is downloaded
- **WHEN** the downloaded `SKILL.md` fails the skill content scan
- **THEN** the daemon rejects that synced version
- **AND** the previous installed skill version remains on disk

#### Scenario: Reject synced skill version with unsafe resource file
- **GIVEN** a signed system skill update includes resource files
- **WHEN** any downloaded resource file fails the skill content scan
- **THEN** the daemon rejects that synced version
- **AND** no partial replacement is left on disk

#### Scenario: Sync rejection is logged with reason
- **WHEN** a system skill update is rejected by content scanning
- **THEN** the daemon logs the skill name and rejection reason
- **AND** the sync loop continues processing other skills
