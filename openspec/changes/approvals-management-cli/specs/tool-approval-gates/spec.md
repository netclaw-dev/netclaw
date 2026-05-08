## MODIFIED Requirements

### Requirement: Persistent approval storage

The system SHALL store persistent approvals ("Approve Always" decisions) in
`~/.netclaw/config/tool-approvals.json`, separate from `netclaw.json`. The file
SHALL NOT be monitored by `ConfigWatcherService`. The file SHALL contain
per-audience sections with per-tool approval lists. For the shipped MVP shell
flow, the lists SHALL contain exact approvals and directory roots as applicable.
Approval lookup and recording SHALL be mediated by `IToolApprovalService`.

The file SHALL also be operator-editable via the `netclaw approvals` CLI
(see the `netclaw-cli` capability). The daemon SHALL pick up out-of-band
edits — whether made by direct file editing or by the CLI — on the next
approval check, without requiring a restart.

#### Scenario: Approve always persists directory root to file

- **GIVEN** the user clicks "Approve Always" for a command targeting
  `/home/.netclaw/logs/crash.log`
- **WHEN** the approval is processed
- **THEN** `/home/.netclaw/logs/` is added to the Personal `shell_execute` list
  in `tool-approvals.json`
- **AND** the daemon does NOT restart

#### Scenario: Persistent approvals loaded at startup

- **GIVEN** `tool-approvals.json` contains
  `{"personal":{"shell_execute":["git push", "/home/.netclaw/logs/"]}}`
- **WHEN** the daemon starts
- **THEN** `git push` is pre-approved for Personal audience shell commands
- **AND** later shell approval units whose recognized local paths all stay under
  `/home/.netclaw/logs/` are pre-approved

#### Scenario: Approve once is retry-scoped only

- **GIVEN** the user clicks "Approve Once" for pattern `docker build`
- **WHEN** the approval is processed
- **THEN** the blocked `docker build` call is retried immediately
- **AND** a later `docker build` call in the same session prompts again
- **AND** `tool-approvals.json` is NOT modified

#### Scenario: Approve for this chat stores directory root in session

- **GIVEN** the user clicks "Approve For This Chat" for a command targeting
  `/home/.netclaw/logs/daemon.log`
- **WHEN** the approval is processed
- **THEN** the directory root is approved for the current session only
- **AND** `tool-approvals.json` is NOT modified
- **AND** a new session will prompt again

#### Scenario: Operator-applied revocation visible without restart

- **GIVEN** the daemon is running with a persisted approval for `git push`
- **WHEN** an operator removes that entry via `netclaw approvals revoke`
- **AND** a new approval check evaluates `git push`
- **THEN** the daemon re-loads the file and observes the entry is gone
- **AND** the user is prompted for approval again
- **AND** the daemon was not restarted
