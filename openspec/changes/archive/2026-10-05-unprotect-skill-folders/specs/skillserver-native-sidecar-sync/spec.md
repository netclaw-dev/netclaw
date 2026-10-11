## ADDED Requirements

### Requirement: RFC skill sync restores a locally changed skill

The agent can write to a server feed skill folder (owner decision,
2026-10-05). For each synced RFC skill, the sync state SHALL record the SHA-256
of each installed file by its relative path. When the published version and
digest of a skill are unchanged, the sync SHALL compare the skill folder with
this record. A changed, added, or removed file, a link, or a file that NetClaw
cannot read SHALL make the sync install the published version again. NetClaw
SHALL log a warning that names the skill and the feed. A record without file
hashes, which an older daemon wrote, SHALL cause one new install that records
them.

#### Scenario: An unchanged skill is not downloaded again

- **GIVEN** a feed skill that NetClaw synced, with the same published version and digest
- **AND** the skill folder matches the recorded file hashes
- **WHEN** server feed sync runs
- **THEN** NetClaw counts the skill as unchanged and does not install it again

#### Scenario: A locally changed skill is restored

- **GIVEN** a feed skill that NetClaw synced, with the same published version and digest
- **AND** the agent changed `scripts/audit.sh` and added `added.md` in the skill folder
- **WHEN** server feed sync runs
- **THEN** NetClaw installs the published version again
- **AND** `scripts/audit.sh` has the published content and `added.md` does not exist
- **AND** NetClaw logs a warning that names the skill

#### Scenario: A failed download keeps the local files

- **GIVEN** a locally changed feed skill
- **WHEN** server feed sync runs and the skill download fails
- **THEN** NetClaw keeps the existing files and counts the skill as failed
